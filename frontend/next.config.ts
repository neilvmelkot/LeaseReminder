import type { NextConfig } from "next";

/**
 * Next.js configuration.
 *
 * The security headers here are the frontend's counterpart to
 * SecurityHeadersMiddleware in the API (section 4.F). They are set in the
 * framework rather than at the proxy, so they travel with the application and
 * are reviewed alongside it.
 */

/**
 * Content Security Policy.
 *
 * Necessarily looser than the API's `default-src 'none'`, because this is a
 * real UI that loads scripts and styles. It is still deny-by-default, with
 * each allowance justified:
 *
 *   script-src 'self' 'unsafe-inline'
 *       Next.js inlines a bootstrap script and the server-rendered flight
 *       payload. A nonce would be stricter and is the right next step, but it
 *       requires generating one per request in the proxy and threading it
 *       through; doing that badly produces a policy that looks strict and
 *       silently falls back. Noted in docs/security.md as a known gap.
 *
 *       In development only, 'unsafe-eval' is added. React's development
 *       build uses eval() to reconstruct call stacks for the error overlay,
 *       and without it every runtime error is replaced by a CSP complaint
 *       about eval, which hides the error you actually wanted to read. React
 *       does not use eval() in a production build, so the production policy
 *       is unchanged and still forbids it. The branch reads NODE_ENV once,
 *       when the config module is loaded, so a production server never
 *       computes the looser string.
 *   style-src 'self' 'unsafe-inline'
 *       Tailwind emits a stylesheet, but React still sets inline styles for
 *       a handful of layout values.
 *   connect-src
 *       The API and Supabase Auth, and nothing else. This is the clause that
 *       matters most: it means a script that did somehow execute could not
 *       post lease data to a third-party host.
 *   img-src / frame-src
 *       Self, plus the Supabase project origin, because lease documents are
 *       served from Storage through short-lived signed URLs and the review
 *       screen displays them. The frame is sandboxed with no allowances, so a
 *       PDF carrying script executes nothing.
 *   frame-ancestors 'none'
 *       Clickjacking. The modern replacement for X-Frame-Options. Not the
 *       same clause as frame-src: this is who may frame us, that is what we
 *       may frame.
 *   object-src 'none', base-uri 'none'
 *       Plugin content and base-tag hijacking, neither of which this app needs.
 */
const isDevelopment = process.env.NODE_ENV !== "production";

function contentSecurityPolicy(): string {
  const supabaseUrl = process.env.NEXT_PUBLIC_SUPABASE_URL ?? "";
  const apiUrl = process.env.NEXT_PUBLIC_API_BASE_URL ?? "";

  const connectSources = ["'self'", supabaseUrl, apiUrl].filter(Boolean).join(" ");

  // Lease documents are served from Supabase Storage, on the project's own
  // origin. Listing the project URL rather than a wildcard keeps the
  // allowance to exactly the host that holds this deployment's documents.
  const storageSources = [supabaseUrl].filter(Boolean).join(" ");

  return [
    "default-src 'self'",
    isDevelopment
      ? "script-src 'self' 'unsafe-inline' 'unsafe-eval'"
      : "script-src 'self' 'unsafe-inline'",
    "style-src 'self' 'unsafe-inline'",
    // The storage origin is added so the document viewer can show a lease.
    // Signed URLs point at Supabase Storage, so a policy of 'self' alone
    // blocks the iframe and the image with no visible error -- the reviewer
    // just sees an empty frame beside the terms they are being asked to
    // approve, which is the worst possible failure for this screen.
    ["img-src 'self' data: blob:", storageSources].filter(Boolean).join(" "),
    "font-src 'self' data:",
    `connect-src ${connectSources}`,
    // Where a PDF may be framed FROM. Distinct from frame-ancestors below,
    // which is about who may frame US. The frame itself is sandboxed with no
    // allowances, so a PDF carrying script gets no origin, no scripts, and no
    // navigation.
    ["frame-src 'self'", storageSources].filter(Boolean).join(" "),
    "frame-ancestors 'none'",
    "object-src 'none'",
    "base-uri 'none'",
    "form-action 'self'",
    "upgrade-insecure-requests",
  ].join("; ");
}

const nextConfig: NextConfig = {
  // Standalone output, so the Docker runtime stage copies a self-contained
  // server instead of the whole node_modules tree. Smaller image, and no
  // development dependencies in production.
  output: "standalone",

  reactStrictMode: true,

  // The framework version is not useful to a client and is useful to an
  // attacker choosing which published vulnerability to try.
  poweredByHeader: false,

  async headers() {
    return [
      {
        source: "/:path*",
        headers: [
          { key: "Content-Security-Policy", value: contentSecurityPolicy() },
          { key: "X-Content-Type-Options", value: "nosniff" },
          { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
          {
            key: "Permissions-Policy",
            value:
              "accelerometer=(), camera=(), geolocation=(), gyroscope=(), " +
              "magnetometer=(), microphone=(), payment=(), usb=()",
          },
          // HSTS. Render terminates TLS in front of this, so the header is
          // what tells the browser never to try HTTP again.
          {
            key: "Strict-Transport-Security",
            value: "max-age=31536000; includeSubDomains",
          },
        ],
      },
      {
        // Lease and tenant pages must not be cached by an intermediary or
        // left in a shared browser's disk cache.
        source: "/((?!_next/static|_next/image|favicon.ico).*)",
        headers: [{ key: "Cache-Control", value: "no-store, must-revalidate" }],
      },
    ];
  },
};

export default nextConfig;
