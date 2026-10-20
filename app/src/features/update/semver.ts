// Compares app versions like 1.0.2 or 1.1.0-rc.1 (a leading v is fine). Anything else is "not newer", so a version
// nobody can read never nags.
const VERSION = /^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z][0-9A-Za-z.-]*))?$/;

type Parsed = { core: number[]; pre: string[] | null };

function parse(version: string | null | undefined): Parsed | null {
  const match = version ? VERSION.exec(version) : null;
  if (!match) return null;
  return { core: [Number(match[1]), Number(match[2]), Number(match[3])], pre: match[4] ? match[4].split('.') : null };
}

const isNumber = (identifier: string) => /^\d+$/.test(identifier);

/** Negative when a < b, positive when a > b, as semver orders pre-release identifiers. */
function comparePre(a: string[], b: string[]): number {
  for (let i = 0; i < Math.min(a.length, b.length); i++) {
    const x = a[i]!;
    const y = b[i]!;
    if (x === y) continue;
    if (isNumber(x) && isNumber(y)) return Number(x) - Number(y);
    if (isNumber(x)) return -1; // numbers sort before words
    if (isNumber(y)) return 1;
    return x < y ? -1 : 1;
  }
  return a.length - b.length;
}

/** True when `candidate` is a later version than `current`. */
export function isNewerVersion(candidate: string | null | undefined, current: string | null | undefined): boolean {
  const a = parse(candidate);
  const b = parse(current);
  if (!a || !b) return false;
  for (let i = 0; i < 3; i++) {
    if (a.core[i] !== b.core[i]) return a.core[i]! > b.core[i]!;
  }
  if (!a.pre && !b.pre) return false;
  if (!a.pre) return true; // 1.1.0 comes after its own 1.1.0-rc.1
  if (!b.pre) return false;
  return comparePre(a.pre, b.pre) > 0;
}
