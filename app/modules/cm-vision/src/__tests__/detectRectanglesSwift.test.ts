// Swift cannot be compiled in CI, so the one promise this change makes to the shelf splitter is checked on the source: with no options the
// rectangle request is the one it always was, and each option changes only the one setting it names.
import fs from 'node:fs';
import path from 'node:path';

const source = fs.readFileSync(path.join(__dirname, '..', '..', 'ios', 'CmVisionModule.swift'), 'utf8');

/** The body of `AsyncFunction("detectRectangles")`, from its header to the closing brace of the request setup. */
function detectRectanglesBody(): string {
  const start = source.indexOf('AsyncFunction("detectRectangles")');
  const end = source.indexOf('AsyncFunction("detectBarcodes")');
  expect(start).toBeGreaterThan(-1);
  expect(end).toBeGreaterThan(start);
  return source.slice(start, end);
}

describe('ios/CmVisionModule.swift detectRectangles', () => {
  it('takes the options as an optional dictionary, as featurePrint takes its crop box', () => {
    expect(detectRectanglesBody()).toMatch(/\(imageUri: String, options: \[String: Double\]\?\)/);
  });

  it('keeps the shelf splitter\'s request when no option is given', () => {
    const body = detectRectanglesBody();
    expect(body).toMatch(/request\.maximumObservations = 0\b/);
    expect(body).toMatch(/request\.minimumConfidence = 0\.5\b/);
    expect(body).toMatch(/request\.minimumSize = 0\.1\b/);
    expect(body).toMatch(/request\.minimumAspectRatio = 0\.1\b/);
    expect(body).toMatch(/request\.quadratureTolerance = 30\b/);
  });

  it('leaves the maximum aspect ratio at Vision\'s own unless the caller names one', () => {
    const body = detectRectanglesBody();
    const assignments = body.match(/request\.maximumAspectRatio\s*=/g) ?? [];
    expect(assignments).toHaveLength(1);
    expect(body).toMatch(/if let maximumAspectRatio = options\?\["maximumAspectRatio"\] \{ request\.maximumAspectRatio = Float\(maximumAspectRatio\) \}/);
  });

  it('changes the minimum size only when the caller names one', () => {
    expect(detectRectanglesBody()).toMatch(/if let minimumSize = options\?\["minimumSize"\] \{ request\.minimumSize = Float\(minimumSize\) \}/);
  });
});
