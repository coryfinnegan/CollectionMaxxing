import schema from '../../contract.schema.json';
// eslint-disable-next-line @typescript-eslint/no-require-imports
const { checkSwiftContract, swiftKeys } = require('../../scripts/check-contract.js');
import { CONTRACT_KEYS, keyListsAreComplete } from '../contract';

type Def = { properties: Record<string, unknown>; required: string[] };
const defs = schema.$defs as unknown as Record<string, Def>;

describe('cm-vision contract', () => {
  it.each(Object.keys(CONTRACT_KEYS))('%s: TypeScript keys equal the schema properties and required list', (name) => {
    const ts = [...CONTRACT_KEYS[name as keyof typeof CONTRACT_KEYS]].sort();
    expect(Object.keys(defs[name].properties).sort()).toEqual(ts);
    expect([...defs[name].required].sort()).toEqual(ts);
  });

  it('compiles only when every interface field is listed (type-level check)', () => {
    expect(keyListsAreComplete).toEqual([true, true, true, true]);
  });

  it('Swift builders emit exactly the schema keys', () => {
    expect(checkSwiftContract()).toEqual([]);
  });

  it('a renamed key in Swift is reported', () => {
    const renamed = swiftKeys('// contract: TextLine\nreturn [\n "text": a,\n "score": b,\n "box": c,\n]\n');
    expect(renamed.TextLine).toEqual(['text', 'score', 'box']);
  });
});
