// The quiet gate: generate, typecheck, lint, test, then a one-line summary per step.
// On failure it prints only the first lines of what broke and exits non-zero.
import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const maxLines = 20;
const logDir = mkdtempSync(join(tmpdir(), 'cm-app-check-'));

const steps = [
  ['generate', 'npm', ['run', 'generate', '--silent']],
  ['typecheck', 'npx', ['tsc', '--noEmit']],
  ['lint', 'npx', ['eslint', 'src', '--max-warnings', '0']],
  ['test', 'npx', ['jest', '--silent', '--reporters=summary', '--ci']],
];

let failed = false;
for (const [name, cmd, args] of steps) {
  const started = Date.now();
  const result = spawnSync(cmd, args, { encoding: 'utf8', shell: true, env: { ...process.env, FORCE_COLOR: '0' } });
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
  const lines = output.split('\n').filter((l) => l.trim() !== '');
  for (const line of lines.slice(0, maxLines)) console.log(`     ${line}`);
  if (lines.length > maxLines) console.log(`     ... ${lines.length - maxLines} more lines`);
  console.log(`     (full output: ${log})`);
  break;
}

process.exit(failed ? 1 : 0);
