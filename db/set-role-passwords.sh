#!/usr/bin/env bash
# =============================================================================
# Sets login passwords for the two application roles.
#
#   ./db/set-role-passwords.sh local
#   ./db/set-role-passwords.sh url "<libpq conninfo>"
#
# The migrations create app_api and app_worker NOLOGIN with no password, on
# purpose: no credential belongs in source control. This is the step that gives
# them one, and it has to be repeated after every ./db/reset.sh, which drops
# the roles so the grants are recreated cleanly.
#
# Passwords are generated here rather than accepted as arguments. Three reasons:
# a generated one is not reused from somewhere else, it never appears in a shell
# history, and it is drawn from an alphabet that needs no escaping in either a
# URI or a semicolon-separated connection string. The quoting rules for those
# two formats differ, and a password containing / @ % : or a space is the most
# common way this step fails.
#
# What it prints is secret. It is shown once, like any generated credential.
# =============================================================================
set -euo pipefail

readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly LOCAL_CONTAINER="leasereminder-postgres"
readonly LOCAL_DB="leasereminder"
readonly PSQL_IMAGE="postgres:17.6-alpine"

MODE="${1:-}"
TARGET="${2:-}"

usage() {
  sed -n '3,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit 64
}

# -- A password that needs no escaping anywhere -------------------------------
#
# Letters and digits only. At 32 characters that is about 190 bits, far beyond
# anything a punctuation mark would add, and it survives a URI, a libpq
# conninfo, a .NET connection string, and a Render environment variable without
# a single escape.
generate_password() {
  local raw

  # head reads a bounded amount and exits; everything downstream consumes all
  # of it. Piping /dev/urandom INTO head instead kills the upstream process
  # with SIGPIPE the moment head has enough, and under "set -o pipefail" that
  # becomes exit 141 and takes the whole script with it.
  raw="$(LC_ALL=C head -c 256 /dev/urandom | base64 | tr -dc 'A-Za-z0-9')"

  if [[ ${#raw} -lt 32 ]]; then
    echo "error: could not generate a password." >&2
    exit 70
  fi

  printf '%s' "${raw:0:32}"
}

run_sql() {
  case "${MODE}" in
    local)
      docker exec -i "${LOCAL_CONTAINER}" \
        psql -v ON_ERROR_STOP=1 --quiet -U postgres -d "${LOCAL_DB}" -c "$1"
      ;;
    url)
      if command -v psql >/dev/null 2>&1; then
        psql -v ON_ERROR_STOP=1 --quiet "${TARGET}" -c "$1"
      else
        MSYS_NO_PATHCONV=1 docker run --rm -i "${PSQL_IMAGE}" \
          psql -v ON_ERROR_STOP=1 --quiet "${TARGET}" -c "$1"
      fi
      ;;
  esac
}

case "${MODE}" in
  local)
    if ! docker exec "${LOCAL_CONTAINER}" pg_isready -U postgres -d "${LOCAL_DB}" >/dev/null 2>&1; then
      echo "error: container ${LOCAL_CONTAINER} is not accepting connections." >&2
      echo "       start it with: docker compose up -d postgres" >&2
      exit 69
    fi
    ;;
  url)
    [[ -n "${TARGET}" ]] || usage

    if ! command -v psql >/dev/null 2>&1; then
      if ! docker info >/dev/null 2>&1; then
        echo "error: this needs either psql on PATH or a running Docker." >&2
        exit 69
      fi

      echo "note: psql is not installed; running it in ${PSQL_IMAGE} instead." >&2
    fi
    ;;
  *)
    usage
    ;;
esac

# -----------------------------------------------------------------------------
# Local development uses a fixed, well-known password on purpose: it is in the
# README and in docker-compose.yml, it protects a database that holds nothing,
# and a generated one would have to be copied into .env after every reset.
# -----------------------------------------------------------------------------
if [[ "${MODE}" == "local" ]]; then
  API_PASSWORD="localdev"
  WORKER_PASSWORD="localdev"
else
  API_PASSWORD="$(generate_password)"
  WORKER_PASSWORD="$(generate_password)"
fi

# The password is interpolated into DDL rather than passed as a parameter,
# because ALTER ROLE does not accept one. It is generated from [A-Za-z0-9]
# above, so there is nothing in it that could close the quote.
run_sql "alter role app_api    with login password '${API_PASSWORD}';" >/dev/null
run_sql "alter role app_worker with login password '${WORKER_PASSWORD}';" >/dev/null

echo
echo "Both roles can now log in."

if [[ "${MODE}" == "local" ]]; then
  echo
  echo "Local development, so both passwords are 'localdev' — the same value"
  echo ".env.example and the README already use. Nothing to copy."
  echo
  exit 0
fi

# -----------------------------------------------------------------------------
# The connection strings, assembled rather than described.
#
# Through the pooler the username carries the project ref, which is the single
# most common thing to get wrong here: app_api on the direct host becomes
# app_api.<ref> on the pooler, and the failure looks exactly like a wrong
# password.
# -----------------------------------------------------------------------------
HOST="$(sed -n 's/.*host=\([^ ]*\).*/\1/p' <<< "${TARGET}")"
PORT="$(sed -n 's/.*port=\([^ ]*\).*/\1/p' <<< "${TARGET}")"
DBNAME="$(sed -n 's/.*dbname=\([^ ]*\).*/\1/p' <<< "${TARGET}")"
OWNER_USER="$(sed -n 's/.*[^a-z]user=\([^ ]*\).*/\1/p' <<< "${TARGET}")"

# postgres.<ref> -> <ref>
PROJECT_REF="${OWNER_USER#*.}"
SUFFIX=""

if [[ "${OWNER_USER}" == *.* ]]; then
  SUFFIX=".${PROJECT_REF}"
fi

echo
echo "================================================================"
echo "  Shown once. Copy them now."
echo "================================================================"
echo
echo "API  (Database__ConnectionString on leasereminder-api):"
echo
echo "Host=${HOST:-YOUR_HOST};Port=${PORT:-5432};Database=${DBNAME:-postgres};Username=app_api${SUFFIX};Password=${API_PASSWORD};SSL Mode=VerifyFull"
echo
echo "Worker (Database__ConnectionString on leasereminder-worker):"
echo
echo "Host=${HOST:-YOUR_HOST};Port=${PORT:-5432};Database=${DBNAME:-postgres};Username=app_worker${SUFFIX};Password=${WORKER_PASSWORD};SSL Mode=VerifyFull"
echo
echo "================================================================"
echo
echo "These are semicolon key/value strings, not URIs, so nothing in them"
echo "needs percent-encoding. Paste them as printed."
echo
