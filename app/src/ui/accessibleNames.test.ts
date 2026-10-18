// A static guard: every pressable, text input and image in app/src says what it is (docs/11 accessibility pass).
// It reads source files, so a new Pressable without a role, or an Image without a label, fails here before it reaches a device.
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const root = join(__dirname, '..');

function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return entry.name === 'generated' ? [] : sourceFiles(path);
    return /\.tsx$/.test(entry.name) && !/\.test\.tsx$/.test(entry.name) ? [path] : [];
  });
}

/** The text of a JSX opening tag, from `<Name` to its closing `>` (braces balanced). */
function openingTags(source: string, names: string[]): { name: string; tag: string; line: number }[] {
  const found: { name: string; tag: string; line: number }[] = [];
  const re = new RegExp(`<(${names.join('|')})\\b`, 'g');
  let match: RegExpExecArray | null;
  while ((match = re.exec(source))) {
    let depth = 0;
    let end = match.index;
    for (; end < source.length; end++) {
      const c = source[end];
      if (c === '{') depth++;
      else if (c === '}') depth--;
      else if (c === '>' && depth === 0 && source[end - 1] !== '=') break;
    }
    found.push({ name: match[1], tag: source.slice(match.index, end + 1), line: source.slice(0, match.index).split('\n').length });
  }
  return found;
}

/** Decorative elements that may go without a name, each with the reason. Key: `relative/path.tsx:Name`. */
const decorative: Record<string, string> = {
  'features/scan/tray/ReceiptBar.tsx:Image': 'the receipt row is one accessible element with the item title as its label; the thumbnail adds nothing',
};

const hasRole = (tag: string) => /accessibilityRole|\brole=/.test(tag);
const hasName = (tag: string) => /accessibilityLabel|aria-label|\balt=|accessibilityLabelledBy|\bplaceholder=|accessible=\{false\}/.test(tag);

describe('accessible names and roles', () => {
  const files = sourceFiles(root).map((path) => ({ rel: path.slice(root.length + 1).replace(/\\/g, '/'), source: readFileSync(path, 'utf8') }));

  it('finds the source files', () => {
    expect(files.length).toBeGreaterThan(50);
  });

  it('gives every Pressable an accessibilityRole', () => {
    const bad = files.flatMap(({ rel, source }) =>
      openingTags(source, ['Pressable', 'TouchableOpacity', 'TouchableHighlight']).filter((t) => !hasRole(t.tag)).map((t) => `${rel}:${t.line}`),
    );
    expect(bad).toEqual([]);
  });

  it('gives every element with onPress that is not a Pressable a role', () => {
    const bad = files.flatMap(({ rel, source }) =>
      openingTags(source, ['Text', 'RNText', 'View', 'Animated.View', 'Image'])
        .filter((t) => /\bonPress=/.test(t.tag) && !hasRole(t.tag))
        .map((t) => `${rel}:${t.line}`),
    );
    expect(bad).toEqual([]);
  });

  it('gives every TextInput an accessibility label', () => {
    const bad = files.flatMap(({ rel, source }) =>
      openingTags(source, ['TextInput']).filter((t) => !hasName(t.tag) && !/\.\.\.rest/.test(t.tag)).map((t) => `${rel}:${t.line}`),
    );
    expect(bad).toEqual([]);
  });

  it('gives every Image a label or marks it decorative with a reason', () => {
    const bad = files.flatMap(({ rel, source }) =>
      openingTags(source, ['Image'])
        .filter((t) => !hasName(t.tag) && !decorative[`${rel}:${t.name}`])
        .map((t) => `${rel}:${t.line}`),
    );
    expect(bad).toEqual([]);
  });

  it('keeps every decorative allow-list entry in use', () => {
    const used = new Set(files.flatMap(({ rel, source }) => openingTags(source, ['Image']).filter((t) => !hasName(t.tag)).map((t) => `${rel}:${t.name}`)));
    expect(Object.keys(decorative).filter((key) => !used.has(key))).toEqual([]);
  });
});
