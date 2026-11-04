#!/usr/bin/env bash
# Runs the SQL test suites in db/tests against a migrated database.
#   ./db/test.sh local
#   ./db/test.sh url "<libpq url>"
set -euo pipefail

readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly TESTS_DIR="${SCRIPT_DIR}/tests"
readonly LOCAL_CONTAINER="leasereminder-postgres"
readonly LOCAL_DB="leasereminder"

MODE="${1:-local}"
TARGET="${2:-}"

failed=0

for path in "${TESTS_DIR}"/*.sql; do
  name="$(basename "${path}")"
  echo "=============================================================="
  echo "Running ${name}"
  echo "=============================================================="

  case "${MODE}" in
    local)
      # ON_ERROR_STOP makes a raised assertion abort the file with exit 3.
      if MSYS_NO_PATHCONV=1 docker exec -i "${LOCAL_CONTAINER}" \
           psql -v ON_ERROR_STOP=1 -U postgres -d "${LOCAL_DB}" < "${path}"; then
        echo "PASS ${name}"
      else
        echo "FAIL ${name}" >&2
        failed=$((failed + 1))
      fi
      ;;
    url)
      if psql -v ON_ERROR_STOP=1 "${TARGET}" -f "${path}"; then
        echo "PASS ${name}"
      else
        echo "FAIL ${name}" >&2
        failed=$((failed + 1))
      fi
      ;;
    *)
      echo "usage: $0 [local|url] [url]" >&2
      exit 64
      ;;
  esac
  echo
done

if [[ "${failed}" -gt 0 ]]; then
  echo "${failed} suite(s) failed." >&2
  exit 1
fi
echo "All SQL suites passed."
