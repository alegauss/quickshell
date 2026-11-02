// QS385. Reads the cold-start verdict out of benchmarks/results/startup-h.md and writes it to
// src/lib/measured.generated.ts, before every build and every dev server (the npm pre-scripts).
//
// A module and not an import of the Markdown: vite.config.ts imports the site's copy for its
// theme key, so whatever the copy imports is evaluated while the config loads, before any Vite
// plugin or define applies. A plain file the copy imports works at that moment too.
//
// A results file whose verdict no longer reads the way this expects stops the build. Falling back
// to a typed number is the thing this exists to prevent.
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const siteDir = join(dirname(fileURLToPath(import.meta.url)), "..");
// Another results file can be named, which is how the refusal below is checked.
const source = process.argv[2] ?? join(siteDir, "..", "benchmarks", "results", "startup-h.md");
const target = join(siteDir, "src", "lib", "measured.generated.ts");

const text = readFileSync(source, "utf8");

function read(pattern, what) {
  const found = pattern.exec(text);
  if (!found) {
    throw new Error(`benchmarks/results/startup-h.md no longer states ${what} where the site reads it (${pattern})`);
  }
  return found[1];
}

const measured = {
  ms: Number(read(/reaches the prompt in \*\*([\d,]+) ms\*\* warm/, "the warm figure").replace(",", "")),
  on: read(/on\s+\*\*(\d{4}-\d{2}-\d{2})\*\*/, "the date"),
  machine: read(/reference machine \(([^,]+),/, "the machine"),
};

writeFileSync(
  target,
  "// Written by scripts/measured.mjs from benchmarks/results/startup-h.md. Not edited by hand.\n"
    + `export const startMeasuredMs = ${measured.ms};\n`
    + `export const startMeasuredOn = ${JSON.stringify(measured.on)};\n`
    + `export const startMachine = ${JSON.stringify(measured.machine)};\n`,
);

console.log(`measured: cold start ${measured.ms} ms on ${measured.on} (${measured.machine})`);
