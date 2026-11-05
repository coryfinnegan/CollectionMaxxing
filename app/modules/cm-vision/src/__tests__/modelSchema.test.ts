import fs from 'node:fs';
import path from 'node:path';

// The picture reader generates the shape that fits the shot: four fields per spine on a shelf, the cover
// fields (without visibleText) on a single item. The OCR-text extractor keeps the full item. Read from the
// Swift sources, the way contract.test.ts does, because there is no Swift test target.
const iosDir = path.join(__dirname, '..', '..', 'ios');
const swift = (name: string) => fs.readFileSync(path.join(iosDir, name), 'utf8');
const schema = JSON.parse(fs.readFileSync(path.join(__dirname, '..', '..', 'contract.schema.json'), 'utf8'));

/** The `var` names and types of a Swift struct, in declaration order. */
function fieldsOf(name: string): { name: string; type: string }[] {
  const match = new RegExp(`struct ${name}\\s*\\{([^}]*)\\}`).exec(swift('CmGenerable.swift'));
  if (!match) throw new Error(`no struct ${name}`);
  return [...match[1].matchAll(/var\s+(\w+):\s*([^\n]+)/g)].map((m) => ({ name: m[1], type: m[2].trim() }));
}
const names = (struct: string) => fieldsOf(struct).map((f) => f.name);

describe('the picture reader generates a lean shape', () => {
  it('a shelf spine generates only title, creator, kind and confidence, in that order', () => {
    expect(names('CmGenSpine')).toEqual(['title', 'creator', 'kind', 'confidence']);
  });

  it('every generated field is a ModelItem key', () => {
    const keys = Object.keys(schema.$defs.ModelItem.properties);
    for (const struct of ['CmGenSpine']) {
      for (const field of names(struct)) expect(keys).toContain(field);
    }
  });

  it('the enums are the shared contract enums', () => {
    expect(fieldsOf('CmGenSpine').find((f) => f.name === 'kind')?.type).toBe('CmGenKind');
    expect(fieldsOf('CmGenSpine').find((f) => f.name === 'confidence')?.type).toBe('CmGenConfidence');
  });

  it('each shape is wrapped in a read of items', () => {
    const src = swift('CmGenerable.swift');
    expect(src).toMatch(/struct CmGenSpineRead\s*\{\s*var items: \[CmGenSpine\]/);
  });

  it('the OCR-text extractor still generates visibleText', () => {
    expect(names('CmGenItem')).toContain('visibleText');
    expect(swift('CmTextReader.swift')).toContain('CmGenRead.self');
    expect(swift('CmTextReader.swift')).not.toMatch(/CmGenSpine|CmGenCover/);
  });

  it('the picture reader picks the shape from the explicit schema option, not from maxItems', () => {
    const request = swift('CmGenerable.swift');
    expect(request).toMatch(/options\["schema"\]/);
    expect(request).toMatch(/spine\s*=/);
    const src = swift('CmModelReader.swift');
    expect(src).toContain('CmGenSpineRead.self');
    expect(src).toContain('CmGenCoverRead.self');
    expect(src).not.toMatch(/maxItems\s*==\s*1/);
  });

  it('the full shape is the default, and a cover item writes no visibleText of its own', () => {
    expect(swift('CmGenerable.swift')).toMatch(/\] as\? String\) == "spine"/);
    expect(names('CmGenCover')).toEqual(['title', 'creator', 'kind', 'platform', 'editionMarks', 'completeness', 'confidence']);
    expect(swift('CmGenerable.swift')).toMatch(/struct CmGenCoverRead\s*\{\s*var items: \[CmGenCover\]/);
    expect(swift('CmModelReader.swift')).toContain('CmGenCoverRead.self');
    expect(swift('CmModelReader.swift')).not.toMatch(/CmGenRead\.self|CmGenItem/);
  });

  it('a cover item hands TypeScript its title as its visibleText', () => {
    expect(swift('CmGenerable.swift')).toMatch(/static func add\(_ partial: CmGenCover\.PartiallyGenerated[\s\S]*?visibleText:\s*title/);
  });

  it('a lean spine hands TypeScript the title as its visibleText and leaves the missing fields out', () => {
    const src = swift('CmGenerable.swift');
    expect(src).toMatch(/visibleText:\s*title/);
    expect(src).toMatch(/platform:\s*nil/);
  });
});


describe('the picture reader warms the session for the next read', () => {
  const src = swift('CmModelReader.swift');

  it('warms the session for the next read and never streams twice from one session', () => {
    expect(src).toMatch(/\.prewarm\(/);
    // The warmed session leaves the slot when a read takes it, so it answers one read only.
    expect(src).toMatch(/func take\(/);
    expect(src).toMatch(/session\s*=\s*nil/);
    // One stream per shape, each on a session that has answered nothing before.
    expect(src.match(/streamResponse\(/g)?.length).toBe(2);
  });

  it('holds at most one warmed session, behind a lock, and only for identical instructions', () => {
    expect(src).toMatch(/NSLock\(\)/);
    expect(src).toMatch(/instructions\s*==\s*/);
  });
});

describe('a read that loops stops itself', () => {
  const gen = swift('CmGenerable.swift');

  it('reads the repeatStop option and clamps anything below 3 up to 3', () => {
    expect(gen).toMatch(/options\["repeatStop"\]/);
    expect(gen).toMatch(/static let minRepeatStop = 3/);
    expect(gen).toMatch(/max\(repeats, CmReadRun\.minRepeatStop\)/);
    expect(gen).toMatch(/max\(\$0, CmReadRun\.minRepeatStop\)/);
  });

  it('counts the run as full when the last N items share title, creator and kind, ignoring case and spacing', () => {
    expect(gen).toMatch(/var isFull: Bool \{[\s\S]*?return fullLocked/);
    expect(gen).toMatch(/items\.suffix\(n\)/);
    expect(gen).toMatch(/\["title", "creator", "kind"\]/);
    expect(gen).toContain('.lowercased()');
    expect(gen).toContain('isWhitespace');
  });

  it('stops taking items once full, and both readers hand the option to the run', () => {
    expect(gen).toMatch(/if closed \|\| fullLocked/);
    expect(swift('CmModelReader.swift')).toContain('repeatStop: request.repeatStop');
    expect(swift('CmTextReader.swift')).toContain('repeatStop: request.repeatStop');
  });

  it('every stream loop returns ok as soon as the run is full', () => {
    expect(swift('CmModelReader.swift').match(/if run\.isFull \{ return \.ok \}/g)).toHaveLength(2);
    expect(swift('CmTextReader.swift')).toContain('if run.isFull { return .ok }');
  });
});
