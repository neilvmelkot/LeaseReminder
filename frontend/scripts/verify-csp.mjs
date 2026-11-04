/**
 * Asserts the production security headers.
 *
 * <h3>Why this exists</h3>
 *
 * The Content-Security-Policy in next.config.ts differs between development
 * and production: development adds 'unsafe-eval', because React's development
 * build uses eval() to reconstruct call stacks for its error overlay, and
 * without the allowance every runtime error is replaced by a CSP complaint
 * about eval. Production forbids it.
 *
 * That difference is one ternary. It is the kind of thing a later edit
 * flattens by accident, and the failure is silent: the application works
 * perfectly either way, and the only symptom is a weaker policy in the
 * environment where it matters. So it is asserted rather than trusted.
 *
 * Run with NODE_ENV=production, since that is the branch under test:
 *
 *   NODE_ENV=production node scripts/verify-csp.mjs
 */

if (process.env.NODE_ENV !== "production") {
  console.error(
    `This check must run with NODE_ENV=production; it was "${process.env.NODE_ENV}".`,
  );
  process.exit(2);
}

const config = (await import("../next.config.ts")).default;

if (typeof config.headers !== "function") {
  console.error("FAIL: next.config.ts does not configure any headers.");
  process.exit(1);
}

const entries = await config.headers();
const headers = entries.flatMap((entry) => entry.headers ?? []);

const byKey = (name) =>
  headers.find((header) => header.key.toLowerCase() === name.toLowerCase())?.value;

let failures = 0;

function check(description, condition) {
  if (condition) {
    console.log(`  ok    ${description}`);
  } else {
    console.error(`  FAIL  ${description}`);
    failures += 1;
  }
}

const csp = byKey("Content-Security-Policy");

console.log("Production security headers\n");

if (!csp) {
  console.error("  FAIL  a Content-Security-Policy header is configured");
  process.exit(1);
}

console.log(`  policy: ${csp}\n`);

// The allowance that must never reach production. React does not use eval()
// in a production build, so there is nothing legitimate asking for it here.
check("CSP does not permit unsafe-eval", !csp.includes("unsafe-eval"));

// Deny-by-default. Without this, every unlisted fetch destination is allowed
// and the rest of the policy is decoration.
check("CSP sets default-src 'self'", csp.includes("default-src 'self'"));

// The clause that stops exfiltration: a script that did execute still could
// not post lease or tenant data to a host that is not named here.
check("CSP restricts connect-src", /connect-src [^;]+/.test(csp));

check("CSP forbids framing", csp.includes("frame-ancestors 'none'"));
check("CSP forbids plugin content", csp.includes("object-src 'none'"));
check("CSP forbids base-tag hijacking", csp.includes("base-uri 'none'"));
check("CSP restricts form targets", csp.includes("form-action 'self'"));

// The headers that are not the CSP but travel with it.
check(
  "Referrer-Policy does not leak paths cross-origin",
  ["no-referrer", "same-origin", "strict-origin", "strict-origin-when-cross-origin"].includes(
    byKey("Referrer-Policy") ?? "",
  ),
);

check("X-Content-Type-Options is nosniff", byKey("X-Content-Type-Options") === "nosniff");

check(
  "HSTS is set with a meaningful max-age",
  (() => {
    const hsts = byKey("Strict-Transport-Security");
    const maxAge = hsts && /max-age=(\d+)/.exec(hsts);

    // 180 days. Short enough to back out of, long enough to be worth setting.
    return Boolean(maxAge) && Number(maxAge[1]) >= 15_552_000;
  })(),
);

console.log("");

if (failures > 0) {
  console.error(`${failures} production header check(s) failed.`);
  process.exit(1);
}

console.log("All production header checks passed.");
