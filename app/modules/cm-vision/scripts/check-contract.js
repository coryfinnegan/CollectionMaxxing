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

module.exports = { checkSwiftContract, swiftKeys, swiftPending };

if (require.main === module) {
  const problems = checkSwiftContract();
  if (problems.length > 0) {
    console.error(problems.join('\n'));
    process.exit(1);
  }
  console.log('cm-vision contract: Swift matches the schema');
}
