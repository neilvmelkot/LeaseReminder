#!/usr/bin/env bash
# Builds and tests the backend inside the .NET SDK container.
#
# There is no .NET SDK on this machine, and the production images are built
# from the same SDK image anyway, so the container is the single source of
# truth for "does it compile". Using it locally means a green build here is a
# green build in CI and on Render.
#
#   ./backend/build.sh restore
#   ./backend/build.sh build
#   ./backend/build.sh test
#   ./backend/build.sh format
set -euo pipefail

readonly SDK_IMAGE="mcr.microsoft.com/dotnet/sdk:10.0"
readonly REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COMMAND="${1:-build}"

# A named volume keeps the NuGet cache between runs; otherwise every restore
# re-downloads the whole graph.
docker volume create leasereminder-nuget >/dev/null

run_in_sdk() {
  # The integration tests need the docker-compose PostgreSQL, which is
  # published on a host port. Inside the container "localhost" is the container
  # itself, so the host gateway alias is added and the connection strings are
  # overridden to use it. Without this the integration tests skip rather than
  # fail, which is a quiet way to lose the RLS coverage entirely.
  MSYS_NO_PATHCONV=1 docker run --rm \
    -v "${REPO_ROOT}:/src" \
    -v leasereminder-nuget:/root/.nuget/packages \
    -w /src/backend \
    --add-host=host.docker.internal:host-gateway \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e DOTNET_NOLOGO=1 \
    -e CI="${CI:-}" \
    -e TEST_OWNER_DATABASE_URL="${TEST_OWNER_DATABASE_URL:-Host=host.docker.internal;Port=54329;Database=leasereminder;Username=postgres;Password=localdev;SSL Mode=Disable}" \
    -e TEST_APP_DATABASE_URL="${TEST_APP_DATABASE_URL:-Host=host.docker.internal;Port=54329;Database=leasereminder;Username=app_api;Password=localdev;SSL Mode=Disable}" \
    "${SDK_IMAGE}" \
    bash -lc "$1"
}

# Runs a service, rather than building one.
#
# Separate from run_in_sdk because the requirements differ: a build needs no
# inbound port and no application configuration, a running service needs both.
# It also loads .env, so the configuration documented in docs/setup.md is
# actually in effect.
run_service() {
  local project="$1"
  local port="${2:-}"
  local publish=()

  if [ -n "${port}" ]; then
    publish=(-p "${port}:8080")
  fi

  local env_file=()

  # --env-file is read by the docker CLI itself, not handed to the daemon, so
  # it needs a path the CLI can open. On Git Bash that means a Windows path:
  # the /c/Users/... form works for -v, because the daemon accepts it, but the
  # CLI cannot find a file by it.
  host_path() {
    if command -v cygpath >/dev/null 2>&1; then
      cygpath -w "$1"
    else
      printf "%s" "$1"
    fi
  }

  if [ -f "${REPO_ROOT}/.env" ]; then
    env_file=(--env-file "$(host_path "${REPO_ROOT}/.env")")
  else
    echo "note: no .env found; the service will start with defaults only." >&2
  fi

  # The worker's overrides, loaded second so they win. Both services read the
  # same Database:ConnectionString key and must connect as different roles;
  # without this the worker would run as app_api, which holds table
  # privileges it is specifically not supposed to have.
  if [ "${project}" = "LeaseReminder.Worker" ] && [ -f "${REPO_ROOT}/.env.worker" ]; then
    env_file+=(--env-file "$(host_path "${REPO_ROOT}/.env.worker")")
  elif [ "${project}" = "LeaseReminder.Worker" ]; then
    echo "note: no .env.worker found; the worker will use the API's database role." >&2
    echo "      copy .env.worker.example and fill it in." >&2
  fi

  # -t only when there is a terminal to attach to. Interactively that gives
  # Ctrl+C and coloured output; without it docker refuses to start at all,
  # which would make this unusable from a script or CI.
  local tty_flags=(-i)

  if [ -t 0 ] && [ -t 1 ]; then
    tty_flags=(-i -t)
  fi

  MSYS_NO_PATHCONV=1 docker run --rm "${tty_flags[@]}" \
    -v "${REPO_ROOT}:/src" \
    -v leasereminder-nuget:/root/.nuget/packages \
    -w "/src/backend/src/${project}" \
    --add-host=host.docker.internal:host-gateway \
    "${publish[@]}" \
    "${env_file[@]}" \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e DOTNET_NOLOGO=1 \
    -e ASPNETCORE_URLS="http://+:8080" \
    "${SDK_IMAGE}" \
    bash -lc "dotnet run --no-launch-profile"
}

case "${COMMAND}" in
  restore) run_in_sdk "dotnet restore LeaseReminder.sln" ;;
  build)   run_in_sdk "dotnet build LeaseReminder.sln -c Release" ;;
  test)    run_in_sdk "dotnet test LeaseReminder.sln -c Release --logger 'console;verbosity=normal'" ;;
  format)  run_in_sdk "dotnet format LeaseReminder.sln --verify-no-changes" ;;

  # The API. Health is at /health/ready.
  #
  # The port is overridable because 8080 is popular: XAMPP, Jenkins, and a
  # dozen other things claim it, and "port is already allocated" is a poor
  # first experience. Set API_PORT to move it, and keep
  # NEXT_PUBLIC_API_BASE_URL and Google__RedirectUri in step.
  run)        run_service "LeaseReminder.Api" "${API_PORT:-8080}" ;;

  # The background worker. No inbound port: it only makes outbound calls.
  run-worker) run_service "LeaseReminder.Worker" ;;
  shell)   MSYS_NO_PATHCONV=1 docker run --rm -it \
             -v "${REPO_ROOT}:/src" \
             -v leasereminder-nuget:/root/.nuget/packages \
             -w /src/backend "${SDK_IMAGE}" bash ;;
  *)       echo "usage: $0 [restore|build|test|format|shell|run|run-worker]" >&2; exit 64 ;;
esac
