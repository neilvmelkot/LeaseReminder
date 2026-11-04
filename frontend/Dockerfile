# =============================================================================
# LeaseReminder frontend
#
# Three stages, so the final image contains only the standalone server output:
# no development dependencies, no source, no npm cache. Runs as a non-root
# user (section 4.H).
#
# A note on build-time environment variables. Next inlines NEXT_PUBLIC_ values
# into the client bundle at BUILD time, so they have to be present here rather
# than only at run time. That is safe for the two that are needed, because both
# are public by design: the Supabase URL and the publishable anon key, which
# grants nothing without a valid session. The service-role key is not among
# them and must never be, since a build argument is permanently visible in the
# image history (section 4.B, 4.J).
# =============================================================================

# -- Dependencies -------------------------------------------------------------
FROM node:24-alpine AS deps

WORKDIR /app

# Only the manifests, so this layer is cached until a dependency changes.
COPY package.json package-lock.json ./

# npm ci rather than npm install: it installs exactly the lock file and fails
# if the lock and package.json have drifted, which is what makes the build
# reproducible.
RUN npm ci

# -- Build --------------------------------------------------------------------
FROM node:24-alpine AS build

WORKDIR /app

COPY --from=deps /app/node_modules ./node_modules
COPY . .

ARG NEXT_PUBLIC_SUPABASE_URL
ARG NEXT_PUBLIC_SUPABASE_ANON_KEY
ARG NEXT_PUBLIC_API_BASE_URL

ENV NEXT_PUBLIC_SUPABASE_URL=$NEXT_PUBLIC_SUPABASE_URL \
    NEXT_PUBLIC_SUPABASE_ANON_KEY=$NEXT_PUBLIC_SUPABASE_ANON_KEY \
    NEXT_PUBLIC_API_BASE_URL=$NEXT_PUBLIC_API_BASE_URL \
    NEXT_TELEMETRY_DISABLED=1

RUN npm run build

# -- Runtime ------------------------------------------------------------------
FROM node:24-alpine AS runtime

WORKDIR /app

ENV NODE_ENV=production \
    NEXT_TELEMETRY_DISABLED=1 \
    PORT=3000 \
    HOSTNAME=0.0.0.0

# A dedicated user with a known, stable UID rather than the node image's
# built-in one, so the number in the image matches the number in the
# deployment notes.
RUN addgroup --system --gid 64003 leasereminder \
    && adduser --system --uid 64003 --ingroup leasereminder leasereminder

# output: "standalone" in next.config.ts produces a server with only the
# modules it actually needs, which is why node_modules is not copied here.
COPY --from=build --chown=root:root --chmod=555 /app/.next/standalone ./
COPY --from=build --chown=root:root --chmod=555 /app/.next/static ./.next/static

# Owned by root and not writable by the runtime user. The frontend writes
# nothing to disk, and on Render the filesystem is not durable anyway
# (section 8).
USER 64003:64003

EXPOSE 3000

# wget is already present in the alpine base, so no extra package is needed
# just to answer a health check.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD wget --quiet --spider --tries=1 http://127.0.0.1:3000/api/health || exit 1

# node as PID 1, so SIGTERM reaches it directly and in-flight requests drain
# on a redeploy.
CMD ["node", "server.js"]
