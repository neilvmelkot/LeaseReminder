#!/usr/bin/env bash
# Installs, type-checks, lints, and builds the frontend inside a Node
# container, for the same reason backend/build.sh uses the SDK container: the
# production image is built from this image, so a green run here is a green
# run in CI and on Render.
#
#   ./frontend/build.sh install
#   ./frontend/build.sh typecheck
#   ./frontend/build.sh lint
#   ./frontend/build.sh build
#   ./frontend/build.sh verify-csp
#   ./frontend/build.sh all
set -euo pipefail

readonly NODE_IMAGE="node:24-alpine"
readonly REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COMMAND="${1:-all}"

docker volume create leasereminder-npm >/dev/null
docker volume create leasereminder-node-modules >/dev/null

run_in_node() {
  MSYS_NO_PATHCONV=1 docker run --rm \
    -v "${REPO_ROOT}/frontend:/app" \
    -v leasereminder-node-modules:/app/node_modules \
    -v leasereminder-npm:/root/.npm \
    -w /app \
    -e NEXT_TELEMETRY_DISABLED=1 \
    -e CI="${CI:-}" \
    "${NODE_IMAGE}" \
    sh -lc "$1"
}

# ---------------------------------------------------------------------------
# Why node_modules is a named volume and not part of the bind mount
#
# The source tree is bind-mounted so the container builds exactly the files on
# disk. If node_modules came through that same mount, the container's install
# would write Linux artefacts into the host's tree: a developer on macOS or
# Windows would find their own node_modules replaced by packages built for
# linux-musl, and every host-side `npm run dev` would then fail on a missing
# native binary (Tailwind's oxide module is the one that breaks first) and on
# missing .cmd shims, which on Windows makes `next` and `eslint` resolve to
# whatever happens to be on PATH instead.
#
# Masking the path with a named volume gives the container its own copy. The
# host tree is never written to, and the container keeps its install between
# runs rather than reinstalling every time.
#
# The consequence to remember: the volume is populated by `install`, so a
# dependency change needs `./frontend/build.sh install` before the other
# commands will see it. `all` does that for you.
# ---------------------------------------------------------------------------

verify_csp() {
  run_in_node "NODE_ENV=production node scripts/verify-csp.mjs"
}

case "${COMMAND}" in
  install)    run_in_node "npm install" ;;
  typecheck)  run_in_node "npm run typecheck" ;;
  lint)       run_in_node "npm run lint" ;;
  build)      run_in_node "npm run build" ;;
  verify-csp) verify_csp ;;
  all)
    run_in_node "npm install"
    run_in_node "npm run typecheck && npm run lint && npm run build"
    verify_csp
    ;;
  shell)      MSYS_NO_PATHCONV=1 docker run --rm -it \
                -v "${REPO_ROOT}/frontend:/app" \
                -v leasereminder-node-modules:/app/node_modules \
                -v leasereminder-npm:/root/.npm \
                -w /app "${NODE_IMAGE}" sh ;;
  *)          echo "usage: $0 [install|typecheck|lint|build|verify-csp|all|shell]" >&2
              exit 64 ;;
esac
