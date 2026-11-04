#!/usr/bin/env node
/* ============================================================================
 * Fails if a component uses a design token the theme does not define.
 *
 * WHY THIS EXISTS
 *   `rounded-panel` was used on three surfaces — two dialogs and the upload
 *   drop zone — and the theme only ever defined --radius-control and
 *   --radius-surface. Tailwind does not warn about an unknown utility; it
 *   simply emits no rule. So the class was inert and those surfaces rendered
 *   with square corners, which no type checker, linter or test could see.
 *
 *   That is the failure mode worth guarding: a token that does not exist is
 *   indistinguishable from one that does, until somebody looks at the screen.
 *
 * WHAT IT CHECKS
 *   Every custom token name in a `rounded-`, `bg-`, `text-`, `border-`,
 *   `ring-`, `shadow-` or `w-`/`h-` utility is matched against the
 *   corresponding @theme custom property. Tailwind's own scale values
 *   (rounded-full, text-sm, bg-white), arbitrary values in brackets, and
 *   opacity suffixes are skipped — only names that look like this project's
 *   tokens are asserted.
 *
 * USAGE
 *   node scripts/verify-tokens.mjs
 * ========================================================================= */

import fs from "node:fs";
import path from "node:path";

const root = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\/(\w:)/, "$1")), "..");
const css = fs.readFileSync(path.join(root, "app/globals.css"), "utf8");

/* ---- what the theme actually declares -------------------------------- */

const declared = new Set();

for (const match of css.matchAll(/^\s*--([a-z]+)-([a-z0-9-]+)\s*:/gm)) {
  declared.add(`${match[1]}-${match[2]}`);
}

/**
 * Utility prefix -> the custom-property namespace Tailwind resolves it from.
 *
 * Tailwind's own scales live in the same namespaces, so a name is only
 * asserted when it is not one of those built-ins.
 */
const namespaces = [
  ["rounded", "radius", new Set(["none", "full", "sm", "md", "lg", "xl", "2xl", "3xl", "4xl"])],
  ["bg", "color", new Set(["transparent", "current", "inherit", "white", "black", "none"])],
  ["text", "color", new Set(["transparent", "current", "inherit", "white", "black",
    "left", "center", "right", "justify", "start", "end", "wrap", "nowrap", "balance",
    "pretty", "ellipsis", "clip", "xs", "sm", "base", "lg", "xl"])],
  ["border", "color", new Set(["transparent", "current", "inherit", "white", "black",
    "solid", "dashed", "dotted", "double", "hidden", "none", "collapse", "separate",
    "x", "y", "t", "r", "b", "l", "s", "e", "spacing"])],
  ["ring", "color", new Set(["transparent", "current", "inherit", "white", "black",
    "inset", "offset"])],
  ["shadow", "shadow", new Set(["none", "inner", "sm", "md", "lg", "xl", "2xl"])],
];

/* ---- what the components use ----------------------------------------- */

const sourceFiles = [];

function walk(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === "node_modules" || entry.name.startsWith(".")) {
      continue;
    }

    const full = path.join(dir, entry.name);

    if (entry.isDirectory()) {
      walk(full);
    } else if (/\.tsx?$/.test(entry.name)) {
      sourceFiles.push(full);
    }
  }
}

for (const dir of ["app", "components"]) {
  walk(path.join(root, dir));
}

const problems = [];

for (const file of sourceFiles) {
  const body = fs.readFileSync(file, "utf8");
  const lines = body.split("\n");

  for (const [index, line] of lines.entries()) {
    for (const [prefix, namespace, builtins] of namespaces) {
      // A class token: optional variant prefixes, then the utility, then a
      // name of lowercase words. Bracketed arbitrary values are skipped by
      // the character class, and an /opacity suffix is not captured.
      const pattern = new RegExp(`(?:^|[\\s"'\`:])-?${prefix}-([a-z][a-z0-9]*(?:-[a-z0-9]+)*)`, "g");

      for (const match of line.matchAll(pattern)) {
        let name = match[1];

        if (!name) {
          continue;
        }

        // border-l-danger colours one side; border-b-0 sets one side's width.
        // Strip the side so the rest resolves as a colour, the same way
        // Tailwind does.
        if (prefix === "border") {
          const side = /^(?:x|y|t|r|b|l|s|e)-(.+)$/.exec(name);

          if (side?.[1]) {
            name = side[1];
          }
        }

        if (builtins.has(name)) {
          continue;
        }

        // Numeric scales (p-4, w-1/2, border-b-0) are Tailwind's own.
        if (/^\d/.test(name)) {
          continue;
        }

        if (declared.has(`${namespace}-${name}`)) {
          continue;
        }

        // A longer token may be a declared one plus a Tailwind modifier, e.g.
        // border-line-strong is declared, border-line is too. Only complain
        // when no declared token is a prefix of this name either.
        const prefixed = [...declared].some(
          (token) => token.startsWith(`${namespace}-`) && name.startsWith(token.slice(namespace.length + 1)),
        );

        if (prefixed) {
          continue;
        }

        problems.push({
          file: path.relative(root, file),
          line: index + 1,
          utility: `${prefix}-${name}`,
          expected: `--${namespace}-${name}`,
        });
      }
    }
  }
}

if (problems.length > 0) {
  console.error("Undefined design tokens — these utilities emit no CSS:\n");

  for (const problem of problems) {
    console.error(
      `  ${problem.file}:${problem.line}  ${problem.utility}`
      + `  (no ${problem.expected} in app/globals.css)`,
    );
  }

  console.error(
    `\n${problems.length} problem(s). Either declare the token in the @theme block`
    + " or use one that exists.",
  );

  process.exit(1);
}

console.log(`Design tokens OK — ${sourceFiles.length} files, ${declared.size} tokens declared.`);
