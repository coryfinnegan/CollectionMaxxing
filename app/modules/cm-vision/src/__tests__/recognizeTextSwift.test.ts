// Swift cannot be compiled in CI (only the TestFlight build compiles it), so the promises this change makes are checked on the source: with no options
// the text request is the one it always was, each option changes only the one setting it names, and every line says its pass.
import fs from 'node:fs';
import path from 'node:path';

const ios = (file: string) => fs.readFileSync(path.join(__dirname, '..', '..', 'ios', file), 'utf8');
const image = ios('CmVisionImage.swift');
const module_ = ios('CmVisionModule.swift');

/** The text between `start` and the first `end` after it, or fails the test when either is missing. */
function between(source: string, start: string, end: string): string {
  const from = source.indexOf(start);
  expect(from).toBeGreaterThan(-1);
  const to = source.indexOf(end, from + start.length);
  expect(to).toBeGreaterThan(from);
  return source.slice(from, to);
}

describe('ios/CmVisionImage.swift text options', () => {
  const options = between(image, 'struct CmTextOptions', '\nenum CmText');
  const text = between(image, 'enum CmText', 'final class CmVisionBadOrientationException');

  it('defaults to the request the scanner always made: accurate, no language correction, the whole picture', () => {
    expect(options).toMatch(/var fast = false\b/);
    expect(options).toMatch(/var languageCorrection = false\b/);
    expect(options).toMatch(/var regions: \[CGRect\] = \[\]/);
    expect(options).toMatch(/\binit\(\) \{\}/); // no options given: all of the above
  });

  it('takes the level, the language correction and the regions from the options the caller names, and nothing else', () => {
    expect(options).toMatch(/init\(_ options: \[String: Any\]\?\) throws/);
    expect(options).toMatch(/fast = \(options\["level"\] as\? String\) == "fast"/);
    expect(options).toMatch(/languageCorrection = \(options\["languageCorrection"\] as\? Bool\) \?\? false/);
    expect(options).toMatch(/options\["regions"\]/);
    expect(options).toMatch(/value as\? \[\[String: Any\]\]/);
  });

  it('sets the recognition level and the language correction of every request from the options', () => {
    expect(text).toMatch(/request\.recognitionLevel = options\.fast \? \.fast : \.accurate/);
    expect(text).toMatch(/request\.usesLanguageCorrection = options\.languageCorrection/);
    expect((text.match(/\.recognitionLevel\s*=/g) ?? []).length).toBe(1);
    expect((text.match(/\.usesLanguageCorrection\s*=/g) ?? []).length).toBe(1);
  });

  it('sets a region of interest only for a region, converting the contract\'s top-left box to Vision\'s bottom-left one', () => {
    expect((text.match(/\.regionOfInterest\s*=/g) ?? []).length).toBe(1);
    expect(text).toMatch(/request\.regionOfInterest = CmContract\.visionRegion\(region\)/);
    expect(text).toMatch(/if options\.regions\.isEmpty \{/);
    expect(between(text, 'if options.regions.isEmpty {', '\n    }')).not.toMatch(/regionOfInterest/); // the whole-picture request has none
  });

  it('reads regions only in an upright pass, as the TypeScript side promises', () => {
    expect(text).toMatch(/guard options\.regions\.isEmpty \|\| rotation == 0 else \{ throw CmVisionBadRegionException\(String\(rotation\)\) \}/);
  });

  it('puts the pass and, for a region, its index on every line it returns, and maps no box back itself beyond the turn', () => {
    expect(text).toMatch(/pass: rotation, region: region/);
    expect(text).toMatch(/CmContract\.unrotated\(observation\.boundingBox, degrees: rotation\)/);
    expect(text).toMatch(/lines\(of: request, rotation: rotation, region: index\)/);
    expect(text).toMatch(/lines\(of: request, rotation: rotation, region: nil\)/);
  });
});

describe('ios/CmVisionModule.swift recognizeText', () => {
  const body = between(module_, 'AsyncFunction("recognizeText")', 'AsyncFunction("modelCapabilities")');

  it('takes the options as an optional dictionary after the orientations, as detectRectangles takes its options', () => {
    expect(body).toMatch(/\(imageUri: String, orientations: \[Int\]\?, options: \[String: Any\]\?\)/);
  });

  it('passes the options to every pass and keeps one upright pass as the default', () => {
    expect(body).toMatch(/let textOptions = try CmTextOptions\(options\)/);
    expect(body).toMatch(/\(orientations\?\.isEmpty \?\? true\) \? \[0\] : orientations!/);
    expect(body).toMatch(/CmText\.recognize\(image, rotation: rotation, options: textOptions\)/);
  });
});

describe('ios/CmVisionContract.swift text line', () => {
  it('builds a line with the pass always and the region only when there is one', () => {
    const line = between(ios('CmVisionContract.swift'), '// contract: TextLine', '// contract: FeaturePrint');
    expect(line).toMatch(/static func textLine\(text: String, confidence: Float, box: CGRect, pass: Int, region: Int\?\) -> \[String: Any\]/);
    expect(line).toMatch(/"pass": pass,/);
    expect(line).toMatch(/if let region = region \{ line\["region"\] = region \}/);
  });

  it('turns a region of the contract (top-left origin) into the region of interest Vision takes (bottom-left origin)', () => {
    const geometry = between(ios('CmVisionContract.swift'), 'static func visionRegion', '\n  }');
    expect(geometry).toMatch(/_ region: CGRect\) -> CGRect/);
    expect(geometry).toMatch(/CGRect\(x: region\.minX, y: 1 - region\.maxY, width: region\.width, height: region\.height\)/);
  });
});
