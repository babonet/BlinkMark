#!/usr/bin/env node
/**
 * Dependency audit gate.
 *
 * `npm audit` can only be tuned by severity, which is the wrong axis. A high-severity advisory in
 * a code path this application never loads is less urgent than a moderate one it calls on every
 * navigation, and `--audit-level` cannot tell them apart. Raising the threshold to get a green
 * build silences the second along with the first.
 *
 * So the gate is per-advisory instead. Anything not explicitly reviewed fails the build. Anything
 * reviewed carries a written reason and an expiry date in audit-exceptions.json, and an expired
 * exception fails exactly as loudly as a brand-new advisory — a judgement gets re-made rather
 * than inherited.
 *
 * Two failure modes are deliberately included beyond "a new advisory appeared":
 *
 *   - An exception whose expiry has passed. Otherwise the list becomes permanent by neglect.
 *   - An exception that no longer matches any live advisory. Otherwise the list accumulates
 *     entries nobody can justify removing, and the next reviewer cannot tell which ones still
 *     carry weight.
 *
 * Usage:
 *   npm audit --json --omit=dev | node scripts/audit-gate.mjs --scope="shipped dependencies"
 *
 * The report arrives on stdin rather than being shelled out to. `npm` is a `.cmd` shim on
 * Windows, which recent Node refuses to spawn without a shell, and spawning through a shell
 * concatenates arguments into a command line instead of passing them as a vector. Piping sidesteps
 * both problems and keeps what is being audited visible in the CI step itself.
 */

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const projectRoot = join(dirname(fileURLToPath(import.meta.url)), '..');

const scopeArgument = process.argv.find((argument) => argument.startsWith('--scope='));
const scope = scopeArgument ? scopeArgument.slice('--scope='.length) : 'dependencies';

function readReport() {
  // File descriptor 0 rather than an async stream: this is a script, and reading the whole report
  // before doing anything with it is the entire program.
  const raw = readFileSync(0, 'utf8').trim();

  if (raw.length === 0) {
    console.error('No audit report on stdin. Pipe `npm audit --json` into this script.');
    process.exit(1);
  }

  try {
    return JSON.parse(raw);
  } catch {
    console.error('Could not parse the audit report as JSON. Was --json passed to npm audit?');
    process.exit(1);
  }
}

/** Every distinct advisory in the report, keyed by its GHSA URL. */
function collectAdvisories(report) {
  const found = new Map();

  for (const vulnerability of Object.values(report.vulnerabilities ?? {})) {
    for (const via of vulnerability.via ?? []) {
      // A string entry means "vulnerable because a dependency is"; only object entries carry an
      // advisory of their own, and counting the strings would report the same issue many times.
      if (typeof via === 'string' || !via.url) {
        continue;
      }

      found.set(via.url, {
        url: via.url,
        title: via.title,
        severity: via.severity,
        package: via.name,
      });
    }
  }

  return found;
}

const report = readReport();
const advisories = collectAdvisories(report);

const { exceptions } = JSON.parse(readFileSync(join(projectRoot, 'audit-exceptions.json'), 'utf8'));

const today = new Date().toISOString().slice(0, 10);
const excepted = new Map(exceptions.map((exception) => [exception.advisory, exception]));

const unreviewed = [...advisories.values()].filter((advisory) => !excepted.has(advisory.url));
const expired = exceptions.filter((exception) => exception.expires < today);
const stale = exceptions.filter((exception) => !advisories.has(exception.advisory));

console.log(
  `Audit gate (${scope}): ${advisories.size} advisory(ies), ${exceptions.length} reviewed exception(s).`,
);

for (const exception of exceptions) {
  if (advisories.has(exception.advisory)) {
    console.log(`  reviewed: ${exception.package} — ${exception.title} (expires ${exception.expires})`);
  }
}

let failed = false;

if (unreviewed.length > 0) {
  failed = true;
  console.error(`\n${unreviewed.length} advisory(ies) have not been reviewed:\n`);
  for (const advisory of unreviewed) {
    console.error(`  [${advisory.severity}] ${advisory.package}: ${advisory.title}`);
    console.error(`      ${advisory.url}`);
  }
  console.error(
    '\nUpgrade the dependency. If the vulnerable code path genuinely cannot be reached from this\n' +
      'application, add an entry to frontend/audit-exceptions.json explaining why, with an expiry.',
  );
}

if (expired.length > 0) {
  failed = true;
  console.error(`\n${expired.length} exception(s) have expired and must be re-reviewed:\n`);
  for (const exception of expired) {
    console.error(`  ${exception.package}: ${exception.title} (expired ${exception.expires})`);
  }
}

if (stale.length > 0) {
  // A warning rather than a failure: a stale entry is untidy, not dangerous, and failing the
  // build on it would mean an upstream fix breaks CI for everyone who did nothing wrong.
  console.warn(`\n${stale.length} exception(s) no longer match any advisory and can be deleted:\n`);
  for (const exception of stale) {
    console.warn(`  ${exception.package}: ${exception.advisory}`);
  }
}

if (failed) {
  process.exit(1);
}

console.log('\nAudit gate passed.');
