#!/usr/bin/env bash
# Drops and recreates the LOCAL development database, then re-applies every
# migration from scratch.
#
# Local only, on purpose. It refuses to run against a URL: the point of the
# checksum guard in migrate.sh is that an applied migration is never edited,
# and this script exists so that guard can be satisfied during development by
# rebuilding rather than by mutating a deployed database.
set -euo pipefail

readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly CONTAINER="leasereminder-postgres"
readonly DB="leasereminder"

if ! docker exec "${CONTAINER}" pg_isready -U postgres >/dev/null 2>&1; then
  echo "error: container ${CONTAINER} is not running." >&2
  echo "       start it with: docker compose up -d postgres" >&2
  exit 69
fi

echo "Dropping and recreating ${DB} ..."
docker exec -i "${CONTAINER}" psql -v ON_ERROR_STOP=1 --quiet -U postgres -d postgres \
  -c "drop database if exists ${DB} with (force);"
docker exec -i "${CONTAINER}" psql -v ON_ERROR_STOP=1 --quiet -U postgres -d postgres \
  -c "create database ${DB};"

# The application roles are cluster-wide, not per-database, so drop them too
# and let 0010 recreate them. Otherwise stale grants survive a reset.
docker exec -i "${CONTAINER}" psql -v ON_ERROR_STOP=1 --quiet -U postgres -d postgres <<'SQL'
do $$
begin
  if exists (select 1 from pg_roles where rolname = 'app_api') then
    drop owned by app_api;
    drop role app_api;
  end if;
  if exists (select 1 from pg_roles where rolname = 'app_worker') then
    drop owned by app_worker;
    drop role app_worker;
  end if;
end;
$$;
SQL

echo
exec "${SCRIPT_DIR}/migrate.sh" local
