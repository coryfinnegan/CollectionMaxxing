// The design ratchet (#307): cm-design eslint rules as errors, grandfathered by
// count in design-baseline.json, plus the .agents/.claude skills drift check.
// Run by `npm run check`; run directly with `node scripts/design-gate.mjs`.
// Regenerate the baseline after paying down debt with --update-baseline.
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';
import { existsSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const { buildEntries, compareToBaseline, summarize } = require('./design-baseline.js');

const appDir = join(dirname(fileURLToPath(import.meta.url)), '..');
const baselinePath = join(appDir, 'design-baseline.json');
const ISSUE = 'https://github.com/coryfinnegan/CollectionMaxxing/issues/307';

const defaultReason = (rule) =>
  ({
    'cm-design/no-raw-colors': 'Pre-existing color literal, grandfathered; new literals fail. Follow-up: #307.',
    'cm-design/no-inline-styles': 'Pre-existing token-valued inline style, grandfathered; new files must use StyleSheet or a primitive. Follow-up: #307.',
    'cm-design/no-raw-text': 'Pre-existing raw text, grandfathered; new raw text fails. Follow-up: #307.',
    'cm-design/no-doubled-title': 'Pre-existing doubled title, grandfathered; new ones fail. Follow-up: #307.',
    'cm-design/no-gradients': 'Pre-existing gradient, grandfathered; new ones fail. Follow-up: #307.',
    'cm-design/no-unistyles-third-party-styles': 'Pre-existing Unistyles sheet value in a third-party style prop, grandfathered; new ones fail. Follow-up: #351.',
  })[rule];

function runEslint() {
  const result = spawnSync('npx', ['eslint', '-c', 'eslint.design.config.js', 'src', '--format', 'json'], {
    cwd: appDir,
    encoding: 'utf8',
    shell: true,
    env: { ...process.env, FORCE_COLOR: '0' },
  });
  const stdout = (result.stdout || '').replace(/^\uFEFF/, '');
  if (!stdout.trim()) {
    console.log(`eslint produced no output (exit ${result.status}): ${(result.stderr || '').split('\n').slice(0, 5).join('\n')}`);
    process.exit(1);
  }
  return JSON.parse(stdout);
}

function treeFiles(dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return treeFiles(path);
    return [path];
  });
}

/** The two skill trees must stay identical; returns drift descriptions (empty when in sync). */
function skillsDrift(repoRoot) {
  const sides = ['.agents/skills', '.claude/skills'].map((side) => {
    const base = join(repoRoot, side);
    const files = new Map();
    if (existsSync(base)) {
      for (const path of treeFiles(base)) {
        files.set(relative(base, path), createHash('sha256').update(readFileSync(path)).digest('hex'));
      }
    }
    return { side, files };
  });
  const [agents, claude] = sides;
  const drift = [];
  for (const [rel, hash] of agents.files) {
    if (!claude.files.has(rel)) drift.push(`${rel}: missing under .claude/skills`);
    else if (claude.files.get(rel) !== hash) drift.push(`${rel}: differs between .agents/skills and .claude/skills`);
  }
  for (const rel of claude.files.keys()) {
    if (!agents.files.has(rel)) drift.push(`${rel}: missing under .agents/skills`);
  }
  return drift;
}

const repoRoot = spawnSync('git', ['rev-parse', '--show-toplevel'], { cwd: appDir, encoding: 'utf8', shell: true }).stdout.trim();

const summary = summarize(runEslint(), appDir);
const oldBaseline = existsSync(baselinePath) ? JSON.parse(readFileSync(baselinePath, 'utf8')) : { entries: [] };
const grandfathered = Object.values(summary).reduce((n, rules) => n + Object.values(rules).reduce((m, r) => m + r.count, 0), 0);

if (process.argv.includes('--update-baseline')) {
  const entries = buildEntries(summary, oldBaseline.entries, defaultReason);
  writeFileSync(baselinePath, `${JSON.stringify({ version: 1, followUp: ISSUE, entries }, null, 2)}\n`);
  console.log(`baseline: ${entries.length} file+rule entries, ${grandfathered} grandfathered hits in ${baselinePath}`);
  process.exit(0);
}

const { failures, paidOff } = compareToBaseline(summary, oldBaseline);
for (const { file, rule, count, allowed, messages } of failures) {
  console.log(`${file}: ${rule} ${count} > ${allowed} allowed`);
  for (const m of messages.slice(0, 8)) console.log(`  ${m.line}:${m.column}: ${m.message}`);
  if (messages.length > 8) console.log(`  ... ${messages.length - 8} more`);
}
for (const { file, rule } of paidOff) console.log(`debt paid: ${file} ${rule} is clean (run --update-baseline to shrink the file)`);

const drift = repoRoot && statSync(repoRoot).isDirectory() ? skillsDrift(repoRoot) : [];
for (const d of drift) console.log(`skills drift: ${d}`);

if (failures.length === 0 && drift.length === 0) {
  console.log(`ok: design ratchet (${grandfathered} grandfathered, ${oldBaseline.entries.length} baseline entries, skills in sync)`);
  process.exit(0);
}
process.exit(1);
