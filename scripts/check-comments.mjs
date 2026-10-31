// Comments say what the code does and why, not where the idea came from.
//
// Fails when a C# comment under src/ names an eval run, "the brief", "the owner said" or a "lead decision", points at
// a bare PR or issue number (#123), or cites a task id (SCAN-1e, EVAL-1a, ...). Those belong in the commit message and
// the PR; a reader of the code has no use for them, and they go stale the day the work merges.
//
// It is a ratchet. scripts/comment-history-enforced.txt lists the paths that are already clean: a hit there fails the
// gate. Hits anywhere else under src/ are counted and reported but do not fail, so the cleanup can land folder by
// folder. To widen the ratchet, see the header of that file.
//
// Run by `bash scripts/check.sh format` (and check.ps1); run directly with `node scripts/check-comments.mjs`.
// Flags: `--report` lists every hit in the not-yet-enforced paths, `--all` treats every path as enforced.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..');
const srcRoot = join(repoRoot, 'src');
const enforcedFile = join(repoRoot, 'scripts', 'comment-history-enforced.txt');

// Each rule is tested against the text of one comment line.
export const rules = [
  { name: 'narrative history', pattern: /\b(eval runs?|the brief|the owner said|lead decision)\b/i },
  { name: 'PR or issue number', pattern: /#\d{3,}/ },
  { name: 'task id', pattern: /\b(SCAN|EVAL|AUTH|APP|OPS|ID|CAM|QA)-\d/ },
];

export function findings(commentText) {
  return rules.filter((r) => r.pattern.test(commentText)).map((r) => r.name);
}

// Splits C# source into its comments, one entry per physical line that carries comment text. A small state machine,
// not a regex, so a `//` inside a string ("https://...") or a char literal is not mistaken for a comment.
export function commentLines(source) {
  const out = [];
  let line = 1;
  let state = 'code';
  let rawQuotes = 0; // how many quotes opened the current raw string literal
  let current = '';
  const flush = () => {
    if (current.trim() !== '') out.push({ line, text: current.trim() });
    current = '';
  };
  for (let i = 0; i < source.length; i++) {
    const c = source[i];
    const n = source[i + 1];
    if (c === '\n') {
      if (state === 'line' || state === 'block') flush();
      if (state === 'line') state = 'code';
      line++;
      continue;
    }
    switch (state) {
      case 'code':
        if (c === '/' && n === '/') { state = 'line'; i++; }
        else if (c === '/' && n === '*') { state = 'block'; i++; }
        else if (c === '"') {
          const run = /^"+/.exec(source.slice(i))[0].length;
          if (run >= 3) { state = 'raw'; rawQuotes = run; i += run - 1; }
          else if (/[@]/.test(source[i - 1] ?? '') || (source[i - 1] === '$' && source[i - 2] === '@')) state = 'verbatim';
          else state = 'string';
        }
        else if (c === "'") state = 'char';
        break;
      case 'line':
        current += c;
        break;
      case 'block':
        if (c === '*' && n === '/') { flush(); state = 'code'; i++; }
        else current += c;
        break;
      case 'string':
        if (c === '\\') i++;
        else if (c === '"') state = 'code';
        break;
      case 'verbatim':
        if (c === '"' && n === '"') i++;
        else if (c === '"') state = 'code';
        break;
      case 'raw':
        if (c === '"' && source.startsWith('"'.repeat(rawQuotes), i)) { i += rawQuotes - 1; state = 'code'; }
        break;
      case 'char':
        if (c === '\\') i++;
        else if (c === "'") state = 'code';
        break;
    }
  }
  flush();
  return out;
}

// scripts/comment-history-enforced.txt: `path` lines are enforced prefixes, `!path` lines carve a file or folder out.
function loadEnforced() {
  const enforced = [];
  const except = [];
  for (const raw of readFileSync(enforcedFile, 'utf8').split(/\r?\n/)) {
    const text = raw.replace(/#.*$/, '').trim();
    if (text === '') continue;
    (text.startsWith('!') ? except : enforced).push(text.replace(/^!/, '').replace(/\\/g, '/'));
  }
  return { enforced, except };
}

function isEnforced(path, { enforced, except }) {
  const under = (prefix) => path === prefix || path.startsWith(prefix.endsWith('/') ? prefix : `${prefix}/`);
  return enforced.some(under) && !except.some(under);
}

function* csFiles(dir) {
  for (const name of readdirSync(dir)) {
    if (name === 'bin' || name === 'obj') continue;
    const full = join(dir, name);
    if (statSync(full).isDirectory()) yield* csFiles(full);
    else if (name.endsWith('.cs')) yield full;
  }
}

function main(args) {
  const showReport = args.includes('--report');
  const all = args.includes('--all');
  const config = loadEnforced();
  const failures = [];
  const pending = [];
  let checked = 0;
  for (const file of csFiles(srcRoot)) {
    const path = relative(repoRoot, file).split(sep).join('/');
    const enforced = all || isEnforced(path, config);
    for (const { line, text } of commentLines(readFileSync(file, 'utf8'))) {
      checked++;
      const names = findings(text);
      if (names.length === 0) continue;
      (enforced ? failures : pending).push({ path, line, text, names });
    }
  }
  if (failures.length > 0) {
    console.log(`comments: ${failures.length} comment line(s) name history, not the code (see scripts/check-comments.mjs)`);
    for (const f of failures.slice(0, 15)) console.log(`     ${f.path}:${f.line}: ${f.names.join(', ')}: ${f.text.slice(0, 110)}`);
    if (failures.length > 15) console.log(`     ... and ${failures.length - 15} more`);
    console.log('     Say what the code does and why; the story of how it got here belongs in the commit message.');
    return 1;
  }
  const byFolder = new Map();
  for (const p of pending) {
    const folder = p.path.split('/').slice(0, 3).join('/');
    byFolder.set(folder, (byFolder.get(folder) ?? 0) + 1);
  }
  const pendingNote = pending.length === 0
    ? ''
    : `; ${pending.length} more in paths not yet enforced (report only: node scripts/check-comments.mjs --report)`;
  console.log(`comments: ok, ${checked} comment lines checked${pendingNote}`);
  if (showReport) {
    for (const [folder, n] of [...byFolder].sort()) console.log(`     ${n}\t${folder}`);
    for (const p of pending) console.log(`     ${p.path}:${p.line}: ${p.names.join(', ')}: ${p.text.slice(0, 110)}`);
  }
  return 0;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  process.exitCode = main(process.argv.slice(2));
}
