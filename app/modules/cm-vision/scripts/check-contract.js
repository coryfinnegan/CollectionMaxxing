// Build-time contract check: the keys the Swift builders emit must match contract.schema.json.
// Run by hand or CI with `node modules/cm-vision/scripts/check-contract.js`; Jest runs the same function.
const fs = require('node:fs');
const path = require('node:path');

const moduleDir = path.resolve(__dirname, '..');

// Shapes the Swift side has not implemented yet. Empty since NAT-2b; the Swift check skips a listed shape and
// fails if Swift already emits one that is still listed here.
const swiftPending = [];

function loadSchema() {
  return JSON.parse(fs.readFileSync(path.join(moduleDir, 'contract.schema.json'), 'utf8'));
}

/**
 * Keys of each `// contract: Name` block in the Swift contract file: dictionary-literal lines ("key": value)
 * and assignments (item["key"] = value, how optional keys are added), in source order.
 */
function swiftKeys(swiftSource) {
  const result = {};
  const parts = swiftSource.split(/^\s*\/\/ contract: (\w+)\s*$/m);
  // parts = [preamble, name1, body1, name2, body2, ...]
  for (let i = 1; i < parts.length; i += 2) {
    const keys = [...parts[i + 1].matchAll(/^\s*"([A-Za-z]+)"\s*:|\[\s*"([A-Za-z]+)"\s*\]\s*=(?!=)/gm)].map((m) => m[1] ?? m[2]);
    result[parts[i]] = keys;
  }
  return result;
}

/** Returns a list of human-readable problems; empty when Swift and the schema agree. */
function checkSwiftContract(dir = moduleDir) {
  const schema = loadSchema();
  const swift = swiftKeys(fs.readFileSync(path.join(dir, 'ios', 'CmVisionContract.swift'), 'utf8'));
  const problems = [];
  for (const name of Object.keys(schema.$defs).filter((n) => n !== 'FixtureRecord')) {
    if (swiftPending.includes(name)) {
      if (swift[name]) problems.push(`${name}: Swift now emits it; remove it from swiftPending`);
      continue;
    }
    const expected = Object.keys(schema.$defs[name].properties).sort();
    const actual = [...(swift[name] ?? [])].sort();
    if (JSON.stringify(expected) !== JSON.stringify(actual)) {
      problems.push(`${name}: schema has [${expected}] but Swift emits [${actual}]`);
    }
  }
  return problems;
}

/** The text of the `{ ... }` body that follows `header` (balanced braces), or '' when the header is absent. */
function bodyAfter(source, header) {
  const at = source.search(header);
  if (at === -1) return '';
  const open = source.indexOf('{', at);
  let depth = 0;
  for (let i = open; i < source.length; i++) {
    if (source[i] === '{') depth++;
    else if (source[i] === '}' && --depth === 0) return source.slice(open + 1, i);
  }
  return '';
}

const returned = (body) => [...body.matchAll(/\breturn\s+"([^"\\]*)"/g)].map((m) => m[1]);
const assigned = (source, name) => [...source.matchAll(new RegExp(`\\b${name}\\s*(?:=|:)\\s*"([^"\\\\]*)"`, 'g'))].map((m) => m[1]);

/** Where Swift writes each schema enum field's values. Each takes the joined Swift sources and returns the strings. */
const enumFields = {
  symbology: (src) => returned(bodyAfter(src, /func symbologyName/)),
  kind: (src) => returned(bodyAfter(src, /enum CmGenKind\b/)),
  completeness: (src) => returned(bodyAfter(src, /enum CmGenCompleteness\b/)),
  confidence: (src) => returned(bodyAfter(src, /enum CmGenConfidence\b/)),
  reason: (src) => assigned(src, 'reason'),
  outcome: (src) => [
    ...[...bodyAfter(src, /enum CmOutcome\b/).matchAll(/\bcase\s+([^\n]+)/g)].flatMap((m) => m[1].split(',').map((n) => n.trim())),
    ...assigned(src, 'outcome'),
  ],
  thermalState: (src) => returned(bodyAfter(src, /func thermalName/)),
};

/** Schema fields that have an `enum`, or just the values of one field. */
function schemaEnumFields(field) {
  const found = {};
  for (const def of Object.values(loadSchema().$defs)) {
    for (const [key, prop] of Object.entries(def.properties ?? {})) {
      if (prop.enum) found[key] = prop.enum;
    }
  }
  return field ? found[field] : Object.keys(found);
}

/**
 * Compares the strings Swift uses for each enum field with the schema's enum: any Swift value outside it is wrong,
 * and any schema value Swift never produces is reported too. `input` is the ios directory or {name: source}.
 */
function checkSwiftEnums(input = path.join(moduleDir, 'ios')) {
  const sources =
    typeof input === 'string'
      ? Object.fromEntries(fs.readdirSync(input).filter((f) => f.endsWith('.swift')).map((f) => [f, fs.readFileSync(path.join(input, f), 'utf8')]))
      : input;
  const joined = Object.values(sources).join('\n');
  const problems = [];
  for (const [field, extract] of Object.entries(enumFields)) {
    const allowed = schemaEnumFields(field);
    const used = [...new Set(extract(joined))];
    for (const value of used.filter((v) => !allowed.includes(v))) {
      problems.push(`${field}: Swift uses "${value}", which is not in the schema enum [${allowed}]`);
    }
    for (const value of allowed.filter((v) => !used.includes(v))) {
      problems.push(`${field}: schema value "${value}" is never produced by Swift (Swift has [${used}])`);
    }
  }
  return problems;
}

module.exports = { checkSwiftContract, checkSwiftEnums, enumFields, schemaEnumFields, swiftKeys, swiftPending };

if (require.main === module) {
  const problems = [...checkSwiftContract(), ...checkSwiftEnums()];
  if (problems.length > 0) {
    console.error(problems.join('\n'));
    process.exit(1);
  }
  console.log('cm-vision contract: Swift matches the schema');
}
