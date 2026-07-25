#!/usr/bin/env node
// SPDX-License-Identifier: MIT

import fs from "node:fs";

const [bundlePath, moduleId] = process.argv.slice(2);
if (!bundlePath || !moduleId) {
  console.error("usage: extract-webpack-module.mjs <bundle.js> <module-id>");
  process.exit(2);
}

const source = fs.readFileSync(bundlePath, "utf8");
const patterns = [
  `${moduleId}:function(`,
  `${moduleId}:(`,
];
let marker = -1;
for (const pattern of patterns) {
  marker = source.indexOf(pattern);
  if (marker !== -1) break;
}
if (marker === -1) {
  console.error(`module ${moduleId} not found`);
  process.exit(1);
}

const bodyStart = source.indexOf("{", marker);
let depth = 0;
let quote = null;
let escaped = false;
let lineComment = false;
let blockComment = false;
let bodyEnd = -1;

for (let i = bodyStart; i < source.length; i += 1) {
  const ch = source[i];
  const next = source[i + 1];

  if (lineComment) {
    if (ch === "\n") lineComment = false;
    continue;
  }
  if (blockComment) {
    if (ch === "*" && next === "/") {
      blockComment = false;
      i += 1;
    }
    continue;
  }
  if (quote) {
    if (escaped) {
      escaped = false;
    } else if (ch === "\\") {
      escaped = true;
    } else if (ch === quote) {
      quote = null;
    }
    continue;
  }
  if (ch === "/" && next === "/") {
    lineComment = true;
    i += 1;
    continue;
  }
  if (ch === "/" && next === "*") {
    blockComment = true;
    i += 1;
    continue;
  }
  if (ch === '"' || ch === "'" || ch === "`") {
    quote = ch;
    continue;
  }
  if (ch === "{") depth += 1;
  if (ch === "}") {
    depth -= 1;
    if (depth === 0) {
      bodyEnd = i + 1;
      break;
    }
  }
}

if (bodyEnd === -1) {
  console.error(`unterminated module ${moduleId}`);
  process.exit(1);
}

process.stdout.write(source.slice(marker, bodyEnd));
