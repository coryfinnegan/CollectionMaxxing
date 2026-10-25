// Plain US English, collector's voice (#302): fails when a British spelling or a
// rewritten stiff phrase reappears in the app (strings, comments and tests alike).
// Run by `npm run check`; run directly with `node scripts/banned-words.mjs`.
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');

// Whole words (case-insensitive). `torchMode` (the camera API) and `user_cancelled`
// (Auth0's error) are not whole words, so they never match; `cancelled` stays legal
// as an identifier and is pinned phrase by phrase below instead.
const words = [
  'torch',
  'catalogue',
  'recognise',
  'recognised',
  'colour',
  'colours',
  'favourite',
  'favourites',
  'behaviour',
  'behaviours',
  'normalise',
  'normalised',
  'grey',
  'greys',
  'enrol',
  'enrolment',
  'cheque',
  'cheques',
  'organise',
  'customise',
  'utilise',
  'realise',
  'finalise',
  'specialise',
  'centralise',
  'centre',
  'centres',
  'centred',
  'capitalised',
  'honour',
  'honours',
  'favour',
  'favours',
  'amongst',
  'whilst',
  'serialise',
  'serialised',
  'neighbour',
  'neighbours',
];

// Phrases (case-insensitive, word-bounded): user-facing copy that was rewritten.
const phrases = [
  'carry on',
  'carries on',
  'passkey cancelled',
  'cancelled before anything',
  'cancelled. sent',
  'nothing was changed',
  'marked partial',
  'could not get the photo list',
  'did not take the results',
  'not known yet',
];

const patterns = [...words, ...phrases].map((w) => ({ word: w, re: new RegExp(`\\b${w}\\b`, 'i') }));

function sourceFiles(dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return entry.name === 'generated' || entry.name === 'node_modules' ? [] : sourceFiles(path);
    return /\.(ts|tsx)$/.test(entry.name) ? [path] : [];
  });
}

const files = [...sourceFiles(join(root, 'src')), join(root, 'app.config.ts')];
const hits = [];
for (const path of files) {
  const lines = readFileSync(path, 'utf8').split('\n');
  lines.forEach((line, i) => {
    for (const { word, re } of patterns) {
      if (re.test(line)) hits.push(`${relative(root, path)}:${i + 1}: ${word}`);
    }
  });
}

if (hits.length > 0) {
  console.log('banned words found:');
  for (const hit of hits) console.log(`  ${hit}`);
  process.exit(1);
}
console.log(`ok: no banned words in ${files.length} files`);
