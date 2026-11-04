#!/usr/bin/env bash
# =============================================================================
# Applies the migrations in db/migrations in filename order.
#
# Usage
#   ./db/migrate.sh local                 apply to the docker-compose database
#   ./db/migrate.sh url "<libpq url>"     apply to any database (staging, prod)
#   ./db/migrate.sh status local          show what has been applied
#
# Migrations run as the TABLE OWNER (postgres on Supabase, app_owner on a
# self-hosted instance), never as app_api or app_worker. The owner connection
# string belongs in MIGRATION_DATABASE_URL and is used by CI and operators
# only -- never by a running service.
#
# Each file is applied inside a single transaction with ON_ERROR_STOP, so a
# failed migration leaves nothing behind. Applied filenames and their checksums
# are recorded in schema_migrations; editing an already-applied file is
# rejected rather than silently ignored.
#
# This script never DROPs anything. Destructive changes are written as explicit,
# separately reviewed migrations and deployed with a backup in hand (4.I).
# =============================================================================
set -euo pipefail

readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly MIGRATIONS_DIR="${SCRIPT_DIR}/migrations"
readonly LOCAL_CONTAINER="leasereminder-postgres"
readonly LOCAL_DB="leasereminder"
# Matches docker-compose.yml, so the client and the local server are the same
# version and a version-specific message means something.
readonly PSQL_IMAGE="postgres:17.6-alpine"

MODE="${1:-}"
TARGET="${2:-}"

usage() {
  sed -n '3,18p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit 64
}

# -- Execution backends -------------------------------------------------------
# Both run psql with ON_ERROR_STOP so a failure aborts immediately.

run_sql_file_local() {
  # MSYS_NO_PATHCONV stops Git Bash on Windows from rewriting the container
  # path /migrations/... into a Windows path before docker ever sees it.
  MSYS_NO_PATHCONV=1 docker exec -i "${LOCAL_CONTAINER}" \
    psql -v ON_ERROR_STOP=1 --single-transaction --quiet -U postgres -d "${LOCAL_DB}" -f "/migrations/$1"
}

run_sql_local() {
  docker exec -i "${LOCAL_CONTAINER}" \
    psql -v ON_ERROR_STOP=1 --quiet --no-align --tuples-only \
    -U postgres -d "${LOCAL_DB}" -c "$1"
}

# psql, from wherever it is available.
#
# A local client is used when there is one. Otherwise it runs in the same
# image the local database uses, so a machine with Docker and nothing else can
# still migrate a remote database -- which is the first thing docs/setup.md
# asks anyone to do.
#
# The migrations directory is mounted read-only: this container runs SQL, it
# does not author it.
psql_url() {
  if command -v psql >/dev/null 2>&1; then
    psql "$@"
  else
    MSYS_NO_PATHCONV=1 docker run --rm -i \
      -v "${MIGRATIONS_DIR}:/migrations:ro" \
      "${PSQL_IMAGE}" \
      psql "$@"
  fi
}

# Where the migrations live from psql's point of view. A local client reads
# the host path; the container reads its mount.
psql_migrations_dir() {
  if command -v psql >/dev/null 2>&1; then
    printf '%s' "${MIGRATIONS_DIR}"
  else
    printf '%s' "/migrations"
  fi
}

run_sql_file_url() {
  psql_url -v ON_ERROR_STOP=1 --single-transaction --quiet "${TARGET}" -f "$(psql_migrations_dir)/$1"
}

run_sql_url() {
  psql_url -v ON_ERROR_STOP=1 --quiet --no-align --tuples-only "${TARGET}" -c "$1"
}

case "${MODE}" in
  local)
    if ! docker exec "${LOCAL_CONTAINER}" pg_isready -U postgres -d "${LOCAL_DB}" >/dev/null 2>&1; then
      echo "error: container ${LOCAL_CONTAINER} is not accepting connections." >&2
      echo "       start it with: docker compose up -d postgres" >&2
      exit 69
    fi
    run_file() { run_sql_file_local "$1"; }
    run_cmd()  { run_sql_local "$1"; }
    ;;
  url)
    [[ -n "${TARGET}" ]] || usage
    if ! command -v psql >/dev/null 2>&1 && ! docker info >/dev/null 2>&1; then
      echo "error: url mode needs either psql on PATH or a running Docker." >&2
      echo "       Docker is the easier one: start Docker Desktop and retry." >&2
      exit 69
    fi

    if ! command -v psql >/dev/null 2>&1; then
      echo "note: psql is not installed; running it in ${PSQL_IMAGE} instead." >&2
    fi
    run_file() { run_sql_file_url "$1"; }
    run_cmd()  { run_sql_url "$1"; }
    ;;
  status)
    # Reuse local or url for the connection, then just report.
    shift
    exec "${BASH_SOURCE[0]}" "${1:-local}" "${2:-}" --status-only
    ;;
  *)
    usage
    ;;
esac

# -- Migration ledger ---------------------------------------------------------
run_cmd "
  create table if not exists schema_migrations (
    filename    text primary key,
    checksum    text not null,
    applied_at  timestamptz not null default now(),
    applied_by  text not null default current_user
  );
" >/dev/null

echo "Applying migrations from ${MIGRATIONS_DIR}"
echo

applied_count=0
skipped_count=0

for path in "${MIGRATIONS_DIR}"/*.sql; do
  filename="$(basename "${path}")"

  # sha256sum is present in Git Bash, macOS needs shasum. Support both.
  if command -v sha256sum >/dev/null 2>&1; then
    checksum="$(sha256sum "${path}" | cut -d' ' -f1)"
  else
    checksum="$(shasum -a 256 "${path}" | cut -d' ' -f1)"
  fi

  recorded="$(run_cmd "
    select checksum from schema_migrations where filename = '${filename}';
  " | tr -d '[:space:]')"

  if [[ -n "${recorded}" ]]; then
    if [[ "${recorded}" != "${checksum}" ]]; then
      # An applied migration has been edited. The database and the repository
      # now disagree, and re-running would not reconcile them.
      echo "  ERROR ${filename}" >&2
      echo "        already applied, but its contents have changed." >&2
      echo "        recorded: ${recorded}" >&2
      echo "        on disk:  ${checksum}" >&2
      echo "        Write a new migration instead of editing an applied one." >&2
      exit 65
    fi
    printf '  skip   %s\n' "${filename}"
    skipped_count=$((skipped_count + 1))
    continue
  fi

  printf '  apply  %s ... ' "${filename}"

  # --single-transaction, passed to psql above, makes each migration atomic.
  #
  # It was documented here but never actually passed, so a migration that
  # failed halfway left its completed DDL behind while the ledger recorded
  # nothing — the database then disagreed with both the repository and the
  # ledger, and a re-run met "column already exists".
  if ! run_file "${filename}" >/dev/null; then
    echo "FAILED" >&2
    echo >&2
    echo "Migration ${filename} failed. Nothing from it was committed." >&2
    exit 70
  fi

  run_cmd "
    insert into schema_migrations (filename, checksum)
    values ('${filename}', '${checksum}');
  " >/dev/null

  echo "ok"
  applied_count=$((applied_count + 1))
done

echo
echo "Applied ${applied_count}, already present ${skipped_count}."

# -- Post-migration assertions ------------------------------------------------
# The migrations contain their own verification blocks; these re-check the two
# invariants that matter most, so a drifted database is caught on every run.
echo
echo "Verifying isolation guarantees:"

tables_without_rls="$(run_cmd "
  select coalesce(string_agg(c.relname, ', ' order by c.relname), 'none')
  from pg_class c
  join pg_namespace n on n.oid = c.relnamespace
  where n.nspname = 'public'
    and c.relkind = 'r'
    and c.relname <> 'schema_migrations'
    and not c.relrowsecurity;
" | tr -d '\r')"
echo "  tables in public without RLS: ${tables_without_rls}"

worker_table_grants="$(run_cmd "
  select coalesce(string_agg(distinct table_name, ', '), 'none')
  from information_schema.role_table_grants
  where grantee = 'app_worker' and table_schema = 'public';
" | tr -d '\r')"
echo "  direct table grants to app_worker: ${worker_table_grants}"

if [[ "${tables_without_rls}" != "none" || "${worker_table_grants}" != "none" ]]; then
  echo
  echo "error: isolation verification failed." >&2
  exit 71
fi

echo "  ok"
