// The quiet gate: generate, typecheck, lint, test, then a one-line summary per step.
// On failure it prints only the first lines of what broke and exits non-zero.
import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const maxLines = 20;
const logDir = mkdtempSync(join(tmpdir(), 'cm-app-check-'));
const jestJson = join(logDir, 'jest.json');
const jestTzJson = join(logDir, 'jest-tz.json');

// Timezone-sensitive suites: TZ must be pinned before the jest workers start.
// Assigning process.env.TZ inside a test is too late (the system zone is already
// cached), so those tests pass on Central machines and fail on UTC CI.
// Bare filenames (no slashes) so the patterns match on every platform.
const tzTests = ['dates.test.ts', 'ItemScreens.test.tsx', 'LibraryTable.test.tsx'];
const jsonForStep = { test: jestJson, 'test:tz': jestTzJson };

// CLI --testPathIgnorePatterns replaces jest.config.js, so re-read its list
// instead of duplicating it (duplication once let playwright specs into jest).
// Lazy: node_modules may not exist yet (the install step runs inside the loop),
// and requiring the config would crash with MODULE_NOT_FOUND.
const require = createRequire(import.meta.url);
let baseIgnores;
function getBaseIgnores() {
  baseIgnores ??= require('../jest.config.js').testPathIgnorePatterns ?? ['/node_modules/'];
  return baseIgnores;
}

// The root gate (scripts/check.*) runs this without installing first, so install when needed.
const steps = [
  ...(existsSync('node_modules/orval') ? [] : [['install', 'npm', ['ci', '--silent', '--no-audit', '--no-fund']]]),
  ['generate', 'npm', ['run', 'generate', '--silent']],
  ['typecheck', 'npx', ['tsc', '--noEmit']],
  ['lint', 'npx', ['eslint', 'src', '--max-warnings', '0']],
  ['test', 'npx', () => ['jest', '--silent', '--reporters=summary', '--ci', '--json', `--outputFile=${jestJson}`, '--testPathIgnorePatterns', ...getBaseIgnores(), ...tzTests]],
  ['test:tz', 'npx', ['jest', '--silent', '--reporters=summary', '--ci', '--json', `--outputFile=${jestTzJson}`, ...tzTests], { TZ: 'America/Chicago' }],
];

// The summary reporter hides which test failed, so read the JSON report and name them.
function failedTestNames(reportFile) {
  try {
    const report = JSON.parse(readFileSync(reportFile, 'utf8'));
    return report.testResults.flatMap((file) => {
      const name = file.name.replace(process.cwd(), '').replace(/^[\\/]/, '');
      const tests = file.assertionResults.filter((a) => a.status === 'failed').map((a) => `${name} > ${a.fullName}`);
      return tests.length > 0 || file.status !== 'failed' ? tests : [`${name} (suite failed to run)`];
    });
  } catch {
    return [];
  }
}

let failed = false;
for (const [name, cmd, argsOrFn, extraEnv] of steps) {
  const args = typeof argsOrFn === 'function' ? argsOrFn() : argsOrFn;
  const started = Date.now();
  const result = spawnSync(cmd, args, { encoding: 'utf8', shell: true, env: { ...process.env, FORCE_COLOR: '0', ...extraEnv } });
  const seconds = Math.round((Date.now() - started) / 1000);
  if (result.status === 0) {
    console.log(`ok   ${name} (${seconds}s)`);
    continue;
  }
  failed = true;
  const output = `${result.stdout ?? ''}${result.stderr ?? ''}`;
  const log = join(logDir, `${name}.log`);
  writeFileSync(log, output);
  console.log(`FAIL ${name} (${seconds}s)`);
  if (name === 'test' || name === 'test:tz') for (const t of failedTestNames(jsonForStep[name])) console.log(`     FAILED: ${t}`);
  const lines = output.split('\n').filter((l) => l.trim() !== '');
  for (const line of lines.slice(0, maxLines)) console.log(`     ${line}`);
  if (lines.length > maxLines) console.log(`     ... ${lines.length - maxLines} more lines`);
  console.log(`     (full output: ${log})`);
  break;
}

process.exit(failed ? 1 : 0);
