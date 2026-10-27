// Large text: nothing the person has to read or press is clipped at the biggest Dynamic Type sizes.
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { render } from '@testing-library/react-native';
import { QueryClientProvider } from '@tanstack/react-query';
import { StyleSheet, useWindowDimensions } from 'react-native';
import type { CandidateDto } from '@/api/generated/models';
import { makeQueryClient } from '@/test/render';
import { ResultCards } from './cards/ResultCards';

jest.mock('@/lib/a11y/announce', () => ({ announceForScreenReader: jest.fn() }));
jest.mock('react-native/Libraries/Utilities/useWindowDimensions', () => ({
  __esModule: true,
  default: jest.fn(),
}));

const dimensions = (fontScale: number) =>
  (useWindowDimensions as unknown as jest.Mock).mockReturnValue({ width: 390, height: 844, scale: 3, fontScale });

const card = {
  id: 'c1',
  seq: 1,
  rawCode: '9780441172719',
  resolution: 'matched',
  alreadyOwnedCount: 1,
  ownedAt: [],
  decision: 'undecided',
  release: { id: 'r1', title: 'The Lord of the Rings: The Fellowship of the Ring', format: 'paperback', year: 1990, platform: null, creators: [], coverUrl: null },
} as unknown as CandidateDto;
const actions = { canAdd: true, onOpen: () => undefined, onAdd: () => undefined, onWishlist: () => undefined };
const wrapper = ({ children }: { children: React.ReactNode }) => <QueryClientProvider client={makeQueryClient()}>{children}</QueryClientProvider>;

describe('result cards at large text sizes', () => {
  it('grow with the text size instead of clipping', async () => {
    dimensions(1);
    const normal = await render(<ResultCards cards={[card]} actions={actions} />, { wrapper });
    const base = StyleSheet.flatten(normal.getByTestId('result-cards').props.style).height as number;
    await normal.unmount();

    dimensions(2.5);
    const bigger = await render(<ResultCards cards={[card]} actions={actions} />, { wrapper });
    const large = StyleSheet.flatten(bigger.getByTestId('result-cards').props.style).height as number;
    expect(large).toBeGreaterThan(base * 2);
  });
});

describe('text scaling', () => {
  const files = (dir: string): string[] =>
    readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
      e.isDirectory() ? (e.name === 'generated' ? [] : files(join(dir, e.name))) : /\.tsx?$/.test(e.name) && !/\.test\./.test(e.name) ? [join(dir, e.name)] : [],
    );

  it('never turns off font scaling', () => {
    const offenders = files(join(__dirname, '..', '..')).filter((f) => /allowFontScaling=\{false\}|allowFontScaling: false/.test(readFileSync(f, 'utf8')));
    expect(offenders).toEqual([]);
  });

  it('gives the shared Button a minimum height, not a fixed one', () => {
    const source = readFileSync(join(__dirname, '..', '..', 'ui', 'Button.tsx'), 'utf8');
    expect(source).toMatch(/minHeight: 44/);
    expect(source).not.toMatch(/\bheight: \d/);
  });
});
