import fs from 'node:fs';
import path from 'node:path';

// eslint-disable-next-line @typescript-eslint/no-require-imports
const { checkSwiftSources, MAX_LITERAL_LENGTH } = require('../../scripts/check-swift-sources.js');

const iosDir = path.join(__dirname, '..', '..', 'ios');

describe('Swift sources keep the prompt out of Swift', () => {
  it('the real sources pass', () => {
    expect(checkSwiftSources(iosDir)).toEqual([]);
  });

  it('there are Swift sources to check (the guard is not vacuous)', () => {
    expect(fs.readdirSync(iosDir).filter((f) => f.endsWith('.swift')).length).toBeGreaterThanOrEqual(3);
  });

  it('fails on a string literal longer than the limit', () => {
    const long = 'a'.repeat(MAX_LITERAL_LENGTH + 1);
    const problems = checkSwiftSources({ 'X.swift': `let s = "${long}"\n` });
    expect(problems.join('\n')).toMatch(/X\.swift:1: string literal of \d+ characters/);
  });

  it('allows a literal at the limit', () => {
    const ok = 'a'.repeat(MAX_LITERAL_LENGTH);
    expect(checkSwiftSources({ 'X.swift': `let s = "${ok}"\n` })).toEqual([]);
  });

  it('measures a literal with escaped quotes correctly', () => {
    const long = 'a'.repeat(MAX_LITERAL_LENGTH + 1);
    expect(checkSwiftSources({ 'X.swift': `let s = "say \\"hi\\" ${long}"\n` })).not.toEqual([]);
  });

  it('fails on a multi-line string literal, however short', () => {
    expect(checkSwiftSources({ 'X.swift': 'let s = """\n  hello\n  """\n' }).join('\n')).toMatch(/multi-line string literal/);
  });

  it.each(['you are a careful librarian', 'List every collectible item in this photo', 'ignore previous instructions'])(
    'fails on prompt wording: %s',
    (text) => {
      expect(checkSwiftSources({ 'X.swift': `let s = "${text}"\n` }).join('\n')).toMatch(/prompt wording/);
    },
  );

  it('fails when a comment carries prompt wording too', () => {
    expect(checkSwiftSources({ 'X.swift': '// You are a careful librarian. List every item.\n' }).join('\n')).toMatch(/prompt wording/);
  });

  it.each(['URLSession.shared', 'URLRequest(url: u)', 'NWConnection(', 'import Network', 'https://example.com'])(
    'fails on network use: %s',
    (code) => {
      expect(checkSwiftSources({ 'X.swift': `${code}\n` }).join('\n')).toMatch(/network/);
    },
  );

  it.each(['try data.write(to: url)', 'FileManager.default.createFile(atPath: p, contents: d)'])('fails on a file write: %s', (code) => {
    expect(checkSwiftSources({ 'X.swift': `${code}\n` }).join('\n')).toMatch(/writes a file/);
  });

  it('fails when logging could carry image data', () => {
    expect(checkSwiftSources({ 'X.swift': 'print(cgImage)\n' }).join('\n')).toMatch(/logging/);
    expect(checkSwiftSources({ 'X.swift': 'logger.info("\\(imageData)")\n' }).join('\n')).toMatch(/logging/);
  });

  it('allows a log of counts and durations', () => {
    expect(checkSwiftSources({ 'X.swift': 'logger.info("items=\\(count) ms=\\(elapsed)")\n' })).toEqual([]);
  });

  it('fails on concatenated short literals that are long together', () => {
    const half = 'a'.repeat(MAX_LITERAL_LENGTH - 10);
    const problems = checkSwiftSources({ 'X.swift': `let s = "${half}" + "${half}"\n` });
    expect(problems.join('\n')).toMatch(/X\.swift:1: .*concatenat/);
  });

  it('fails on concatenated literals split across lines', () => {
    const half = 'a'.repeat(MAX_LITERAL_LENGTH - 10);
    expect(checkSwiftSources({ 'X.swift': `let s = "${half}" +\n  "${half}"\n` }).join('\n')).toMatch(/concatenat/);
  });

  it('allows short concatenated literals', () => {
    expect(checkSwiftSources({ 'X.swift': 'let s = "abc" + "def"\n' })).toEqual([]);
  });

  it('fails on a multi-line logger call that interpolates image-like data', () => {
    const code = 'logger.info(\n  "read \\(count)",\n  "\\(imageData)"\n)\n';
    expect(checkSwiftSources({ 'X.swift': code }).join('\n')).toMatch(/image data/);
  });

  it('fails when a nested paren precedes the image-like name', () => {
    expect(checkSwiftSources({ 'X.swift': 'logger.info("n=\\(f(1)) \\(jpegBytes)")\n' }).join('\n')).toMatch(/image data/);
  });

  it('still allows a multi-line logger call with counts only', () => {
    expect(checkSwiftSources({ 'X.swift': 'logger.info(\n  "n=\\(count) ms=\\(ms)"\n)\n' })).toEqual([]);
  });
});
