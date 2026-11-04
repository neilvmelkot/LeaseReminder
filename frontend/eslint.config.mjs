import coreWebVitals from "eslint-config-next/core-web-vitals";
import typescriptConfig from "eslint-config-next/typescript";

/**
 * Flat config.
 *
 * eslint-config-next 16 exports flat configs directly from its subpaths, so
 * they are imported rather than wrapped in FlatCompat. Going through FlatCompat
 * fails outright here: it tries to JSON.stringify the config for validation
 * and the plugin graph contains a circular reference.
 */
const config = [
  ...coreWebVitals,
  ...typescriptConfig,
  {
    ignores: [".next/**", "node_modules/**", "next-env.d.ts"],
  },
  {
    rules: {
      // An unused variable is usually the remains of a half-finished edit.
      // Underscore-prefixed names are the documented escape hatch.
      "@typescript-eslint/no-unused-vars": [
        "error",
        { argsIgnorePattern: "^_", varsIgnorePattern: "^_" },
      ],
      // `any` defeats the point of the strict compiler settings in tsconfig.
      "@typescript-eslint/no-explicit-any": "error",
      // console.log in a server component writes to the production log, where
      // this application's values are lease terms and personal data.
      "no-console": ["error", { allow: ["warn", "error"] }],
    },
  },
  {
    // Build and verification scripts, which are run by a human or by CI and
    // never shipped to a server. Printing what they checked is the entire
    // point of them, so the no-console rule that protects application code
    // from logging lease data would only be worked around here.
    files: ["scripts/**/*.mjs", "scripts/**/*.js"],
    rules: {
      "no-console": "off",
    },
  },
];

export default config;
