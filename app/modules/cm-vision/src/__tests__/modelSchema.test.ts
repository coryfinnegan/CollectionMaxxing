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

  it('a single cover generates no visibleText and keeps platform, editionMarks and completeness', () => {
    expect(names('CmGenCover')).toEqual(['title', 'creator', 'kind', 'platform', 'editionMarks', 'completeness', 'confidence']);
  });

  it('every generated field is a ModelItem key', () => {
    const keys = Object.keys(schema.$defs.ModelItem.properties);
    for (const struct of ['CmGenSpine', 'CmGenCover']) {
      for (const field of names(struct)) expect(keys).toContain(field);
    }
  });

  it('the enums are the shared contract enums', () => {
    expect(fieldsOf('CmGenSpine').find((f) => f.name === 'kind')?.type).toBe('CmGenKind');
    expect(fieldsOf('CmGenSpine').find((f) => f.name === 'confidence')?.type).toBe('CmGenConfidence');
    expect(fieldsOf('CmGenCover').find((f) => f.name === 'completeness')?.type).toBe('CmGenCompleteness?');
  });

  it('each shape is wrapped in a read of items', () => {
    const src = swift('CmGenerable.swift');
    expect(src).toMatch(/struct CmGenSpineRead\s*\{\s*var items: \[CmGenSpine\]/);
    expect(src).toMatch(/struct CmGenCoverRead\s*\{\s*var items: \[CmGenCover\]/);
  });

  it('the OCR-text extractor still generates visibleText', () => {
    expect(names('CmGenItem')).toContain('visibleText');
    expect(swift('CmTextReader.swift')).toContain('CmGenRead.self');
    expect(swift('CmTextReader.swift')).not.toMatch(/CmGenSpine|CmGenCover/);
  });

  it('the picture reader picks the shape per read by maxItems, and no longer generates the full item', () => {
    const src = swift('CmModelReader.swift');
    expect(src).toMatch(/maxItems\s*==\s*1/);
    expect(src).toContain('CmGenSpineRead.self');
    expect(src).toContain('CmGenCoverRead.self');
    expect(src).not.toContain('CmGenRead.self');
  });

  it('a lean item hands TypeScript the title as its visibleText and leaves the missing fields out', () => {
    const src = swift('CmGenerable.swift');
    expect(src).toMatch(/visibleText:\s*title/);
    expect(src).toMatch(/platform:\s*nil/);
  });
});
