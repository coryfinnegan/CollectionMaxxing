import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

import schema from '../../contract.schema.json';
// eslint-disable-next-line @typescript-eslint/no-require-imports
const { checkSwiftContract, checkSwiftEnums, enumFields, schemaEnumFields, swiftKeys, swiftPending } = require('../../scripts/check-contract.js');
import { CONTRACT_KEYS, keyListsAreComplete, OPTIONAL_KEYS } from '../contract';

type Def = { properties: Record<string, unknown>; required: string[] };
const defs = schema.$defs as unknown as Record<string, Def>;

describe('cm-vision contract', () => {
  it.each(Object.keys(CONTRACT_KEYS))('%s: TypeScript keys equal the schema properties and required list', (name) => {
    const ts = [...CONTRACT_KEYS[name as keyof typeof CONTRACT_KEYS]].sort();
    expect(Object.keys(defs[name].properties).sort()).toEqual(ts);
    const optional = OPTIONAL_KEYS[name] ?? [];
    expect([...defs[name].required].sort()).toEqual(ts.filter((k) => !optional.includes(k)));
  });

  it('compiles only when every interface field is listed (type-level check)', () => {
    expect(keyListsAreComplete).toEqual([true, true, true, true, true, true, true, true]);
  });

  it('Swift builders emit exactly the schema keys', () => {
    expect(checkSwiftContract()).toEqual([]);
  });

  it('has no pending shapes: NAT-2b implemented every one in Swift', () => {
    expect(swiftPending).toEqual([]);
  });

  it.each(['ModelItem', 'ModelCapabilities', 'ReadItemsResult', 'DeviceStatus'])('Swift has a contract block for %s', (name) => {
    const source = fs.readFileSync(path.join(__dirname, '..', '..', 'ios', 'CmVisionContract.swift'), 'utf8');
    expect(Object.keys(swiftKeys(source))).toContain(name);
  });

  it('reads keys that are added by assignment, which is how optional keys are written', () => {
    const keys = swiftKeys('// contract: ModelItem\nvar item: [String: Any] = [\n "title": a,\n]\nif let c { item["creator"] = c }\n');
    expect(keys.ModelItem).toEqual(['title', 'creator']);
  });

  it('a Swift key missing from the schema is reported', () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'cm-contract-'));
    fs.mkdirSync(path.join(dir, 'ios'));
    const real = fs.readFileSync(path.join(__dirname, '..', '..', 'ios', 'CmVisionContract.swift'), 'utf8');
    fs.writeFileSync(path.join(dir, 'ios', 'CmVisionContract.swift'), real.replace('"elapsedMs": elapsedMs,', '"elapsed": elapsedMs,'));
    expect(checkSwiftContract(dir).join('\n')).toMatch(/ReadItemsResult/);
  });

  it('every schema shape is either checked against Swift or explicitly pending', () => {
    const shapes = Object.keys(defs).filter((n) => n !== 'FixtureRecord');
    expect(shapes.sort()).toEqual(Object.keys(CONTRACT_KEYS).sort());
  });

  it('a renamed key in Swift is reported', () => {
    const renamed = swiftKeys('// contract: TextLine\nreturn [\n "text": a,\n "score": b,\n "box": c,\n]\n');
    expect(renamed.TextLine).toEqual(['text', 'score', 'box']);
  });

  describe('enum values', () => {
    const iosDir = path.join(__dirname, '..', '..', 'ios');
    const realSources = (): Record<string, string> =>
      Object.fromEntries(fs.readdirSync(iosDir).filter((f) => f.endsWith('.swift')).map((f) => [f, fs.readFileSync(path.join(iosDir, f), 'utf8')]));

    it('the real Swift sources use only schema enum values, and all of them', () => {
      expect(checkSwiftEnums()).toEqual([]);
    });

    it('a Swift enum string that is not in the schema enum is reported', () => {
      const sources = realSources();
      sources['CmGenerable.swift'] = sources['CmGenerable.swift'].replace('case ok, declined, timeout,', 'case ok, declined, timedOut,');
      expect(checkSwiftEnums(sources).join('\n')).toMatch(/outcome.*timedOut/);
    });

    it('a schema enum value that Swift never produces is reported', () => {
      const sources = realSources();
      sources['CmVisionImage.swift'] = sources['CmVisionImage.swift'].replace('return "critical"', 'return "serious"');
      expect(checkSwiftEnums(sources).join('\n')).toMatch(/thermalState.*critical/);
    });

    it('checks every enum field in the schema (outcome, reason, thermalState, kind, ...)', () => {
      const inSchema: string[] = schemaEnumFields().sort();
      expect(inSchema).toEqual(expect.arrayContaining(['outcome', 'reason', 'thermalState', 'kind', 'completeness', 'confidence', 'symbology']));
      expect(Object.keys(enumFields).sort()).toEqual(inSchema);
    });

    it.each(['outcome', 'reason', 'thermalState', 'kind', 'completeness', 'confidence', 'symbology'])('%s: a typo in the Swift value is reported', (field) => {
      const first: string = schemaEnumFields(field)[0];
      const typo = Object.fromEntries(
        Object.entries(realSources()).map(([n, src]) => [n, src.split(`"${first}"`).join('"typo"').replace(new RegExp(`\\b${first}\\b(?=[,\\n])`), 'typo')]),
      );
      expect(checkSwiftEnums(typo).join('\n')).toMatch(new RegExp(field));
    });
  });
});
