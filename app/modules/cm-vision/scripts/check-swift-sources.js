// Build-time guard for the Swift in ios/: the prompt, thresholds and weights live in TypeScript or on the
// server, and photos stay on the phone. This fails on:
//   - a string literal longer than MAX_LITERAL_LENGTH, or any multi-line string literal (where a prompt would go),
//   - wording that belongs in a prompt (also checked in comments, so a prompt cannot hide there),
//   - network APIs and URLs,
//   - code that writes a file or saves to the photo library,
//   - print/NSLog/dump, or a logger line that interpolates something that could be image data (log counts and durations only).
// Run by hand or CI with `node modules/cm-vision/scripts/check-swift-sources.js`; Jest runs the same function.
const fs = require('node:fs');
const path = require('node:path');

const MAX_LITERAL_LENGTH = 80;

/** Lower-case phrases that only appear in a prompt. */
const PROMPT_WORDING = [
  'you are a',
  'you are an',
  'you identify',
  'list every',
  'list all',
  'ignore previous',
  'ignore the above',
  'ignore all',
  'ignore any',
  'respond with',
  'reply with',
  'return only',
  'do not invent',
  "don't invent",
  'never invent',
  'collectible',
  'as printed',
  'every distinct',
  'in this photo',
  'in this picture',
];

const NETWORK = [/\bURLSession\b/, /\bURLRequest\b/, /\bNWConnection\b/, /\bNWPathMonitor\b/, /^\s*import\s+Network\b/, /\bCFNetwork\b/, /https?:\/\//];
const FILE_WRITES = [
  /\.write\(\s*to\s*:/,
  /\.write\(\s*toFile\s*:/,
  /\bcreateFile\s*\(/,
  /\bFileHandle\s*\(\s*forWritingTo/,
  /\bcreateDirectory\s*\(/,
  /\bmoveItem\s*\(/,
  /\bcopyItem\s*\(/,
  /\bUIImageWriteToSavedPhotosAlbum\b/,
  /\bPHPhotoLibrary\b/,
];
const UNSAFE_LOGGING = [/\bprint\s*\(/, /\bdebugPrint\s*\(/, /\bNSLog\s*\(/, /\bdump\s*\(/];
const LOGGER_CALL = /\b(?:logger|log)\.(?:debug|info|notice|warning|error|fault|log|trace)\s*\(/;
const IMAGE_LIKE = /\\\([^)]*(?:image|pixel|bytes|jpeg|png|base64|\bdata\b|cgImage|ciImage|attachment)/i;

function loadSources(input) {
  if (typeof input !== 'string') return input;
  const sources = {};
  for (const name of fs.readdirSync(input).filter((f) => f.endsWith('.swift'))) {
    sources[name] = fs.readFileSync(path.join(input, name), 'utf8');
  }
  return sources;
}

/** Returns a list of human-readable problems; empty when the sources are clean. `input` is a directory or {name: source}. */
function checkSwiftSources(input = path.resolve(__dirname, '..', 'ios')) {
  const problems = [];
  for (const [name, source] of Object.entries(loadSources(input))) {
    if (source.includes('"""')) problems.push(`${name}: multi-line string literal (prompts do not live in Swift)`);
    source.split('\n').forEach((line, index) => {
      const where = `${name}:${index + 1}`;
      for (const match of line.matchAll(/"((?:[^"\\]|\\.)*)"/g)) {
        if (match[1].length > MAX_LITERAL_LENGTH) {
          problems.push(`${where}: string literal of ${match[1].length} characters (limit ${MAX_LITERAL_LENGTH})`);
        }
      }
      const lower = line.toLowerCase();
      for (const phrase of PROMPT_WORDING) {
        if (lower.includes(phrase)) problems.push(`${where}: prompt wording "${phrase}" (prompts come from TypeScript)`);
      }
      if (NETWORK.some((re) => re.test(line))) problems.push(`${where}: network use (the module makes no network calls)`);
      if (FILE_WRITES.some((re) => re.test(line))) problems.push(`${where}: writes a file or the photo library`);
      if (UNSAFE_LOGGING.some((re) => re.test(line))) problems.push(`${where}: logging with print/NSLog/dump (use the logger, counts and durations only)`);
      if (LOGGER_CALL.test(line) && IMAGE_LIKE.test(line)) problems.push(`${where}: logging something that could be image data`);
    });
  }
  return problems;
}

module.exports = { checkSwiftSources, MAX_LITERAL_LENGTH, PROMPT_WORDING };

if (require.main === module) {
  const problems = checkSwiftSources();
  if (problems.length > 0) {
    console.error(problems.join('\n'));
    process.exit(1);
  }
  console.log('cm-vision Swift sources: no prompt text, network, file writes or image logging');
}
