import { fireEvent, render, screen } from '@testing-library/react-native';
import type { CatalogSearchResultDto, ReleaseDto, WorkDto } from '@/api/generated/models';
import { setHapticDriver, type HapticDriver } from '@/lib/haptics';
import { EditionRow, editionFacts, editionName, WorkRow, yearSpanOf } from './rows';

const work = (overrides: Partial<WorkDto> = {}): WorkDto => ({
  id: 'w0000000-0000-4000-8000-000000000001',
  kind: 'book',
  title: 'Suddenly You',
  originalYear: 2001,
  creators: ['Lisa Kleypas'],
  genres: [],
  synopsis: null,
  coverUrl: null,
  ...overrides,
});

const release = (overrides: Partial<ReleaseDto> = {}): ReleaseDto => ({
  id: 'r0000000-0000-4000-8000-000000000001',
  workId: 'w0000000-0000-4000-8000-000000000001',
  title: 'Suddenly You',
  format: 'paperback',
  platform: null,
  region: null,
  edition: null,
  publisher: null,
  releaseDate: null,
  year: 2001,
  creators: ['Lisa Kleypas'],
  pageCount: null,
  runtimeMinutes: null,
  coverUrl: null,
  source: 'user',
  identifiers: [],
  ...overrides,
});

const result = (releases: ReleaseDto[], overrides: Partial<WorkDto> = {}): CatalogSearchResultDto => ({
  work: work(overrides),
  releases,
  score: 0.9,
  source: 'local',
});

const fakeHaptics = () => {
  const calls: string[] = [];
  const driver: HapticDriver = {
    tap: () => void calls.push('tap'),
    mediumTap: () => void calls.push('medium'),
    longTap: () => void calls.push('long'),
    tick: () => void calls.push('tick'),
    success: () => void calls.push('success'),
    wait: async () => undefined,
  };
  setHapticDriver(driver);
  return calls;
};

afterEach(() => setHapticDriver(null));

describe('editionName', () => {
  it('names the edition with its year and the tail that tells editions apart (#440)', () => {
    expect(editionName(release({ year: 2001, publisher: 'Avon' }))).toBe('Suddenly You (2001), Paperback, Avon');
  });

  it('names the platform instead of the format when the release has one', () => {
    const r = release({ title: 'Super Mario 64', format: 'game_cartridge', platform: { id: 'p1', name: 'Nintendo 64', shortName: 'N64', manufacturer: 'Nintendo' }, year: 1996, creators: [] });
    expect(editionName(r)).toBe('Super Mario 64 (1996), Nintendo 64');
  });
});

describe('editionFacts', () => {
  it('lists creators, year, and format where the catalog provides them', () => {
    expect(editionFacts(release({ year: 2001 }))).toBe('Lisa Kleypas · 2001 · Paperback');
  });

  it('adds the edition and publisher when provided', () => {
    expect(editionFacts(release({ edition: 'First edition', publisher: 'Avon' }))).toBe('Lisa Kleypas · 2001 · Paperback · First edition · Avon');
  });

  it('is empty when the catalog provides nothing to tell editions apart', () => {
    const r = release({ creators: [], year: null, format: 'unknown' });
    expect(editionFacts({ ...r, format: undefined as unknown as ReleaseDto['format'] })).toBe('');
  });
});

describe('EditionRow', () => {
  it('shows the title and the catalog facts under it', async () => {
    await render(<EditionRow release={release({ year: 2001 })} onPress={jest.fn()} />);

    expect(screen.getByText('Suddenly You')).toBeTruthy();
    expect(screen.getByText('Lisa Kleypas · 2001 · Paperback')).toBeTruthy();
  });

  it('shows the catalog art named for the edition', async () => {
    await render(<EditionRow release={release({ coverUrl: 'https://example.invalid/cover.jpg' })} onPress={jest.fn()} />);

    expect(screen.getByLabelText('Cover of Suddenly You (2001), Paperback')).toBeTruthy();
  });

  it('falls back to a plain tile, never a text tile, when there is no art anywhere', async () => {
    await render(<EditionRow release={release({ coverUrl: null })} onPress={jest.fn()} />);

    expect(screen.queryByLabelText(/Cover of /)).toBeNull();
    // The row title is the only "Suddenly You" on screen: a text tile would add a second, hidden one.
    expect(screen.getAllByText('Suddenly You', { includeHiddenElements: true })).toHaveLength(1);
  });

  it('prefers the given cover address over the catalog one', async () => {
    await render(
      <EditionRow release={release({ coverUrl: 'https://example.invalid/catalog.jpg' })} coverUri="https://example.invalid/crop.jpg" onPress={jest.fn()} />,
    );

    expect(screen.getByLabelText('Cover of Suddenly You (2001), Paperback').props.source).toEqual({ uri: 'https://example.invalid/crop.jpg' });
  });

  it('shows the suggestion reason when one is given', async () => {
    await render(<EditionRow release={release()} reason="Cover match" onPress={jest.fn()} />);

    expect(screen.getByText('Cover match')).toBeTruthy();
  });

  it('shows how many copies are owned when any are owned', async () => {
    await render(<EditionRow release={release()} ownedCount={2} onPress={jest.fn()} />);

    expect(screen.getByText('You own 2')).toBeTruthy();
  });

  it('taps lightly and picks the edition on press', async () => {
    const calls = fakeHaptics();
    const onPress = jest.fn();
    await render(<EditionRow release={release({ year: 2001 })} onPress={onPress} />);

    await fireEvent.press(screen.getByRole('button', { name: 'Suddenly You (2001), Paperback' }));

    expect(onPress).toHaveBeenCalledTimes(1);
    expect(calls).toEqual(['tap']);
  });

  it('as a review row, offers a radio with a Select label and the selection tick', async () => {
    const calls = fakeHaptics();
    const onPress = jest.fn();
    await render(<EditionRow release={release({ year: 2001 })} selectable selected onPress={onPress} />);

    const radio = screen.getByRole('radio', { name: 'Select Suddenly You (2001), Paperback' });
    expect(radio.props.accessibilityState).toMatchObject({ selected: true });
    await fireEvent.press(radio);

    expect(onPress).toHaveBeenCalledTimes(1);
    expect(calls).toEqual(['tick']);
  });

  it('as a review row, marks a disabled option and stays silent', async () => {
    const calls = fakeHaptics();
    const onPress = jest.fn();
    await render(<EditionRow release={release({ year: 2001 })} selectable disabled onPress={onPress} />);

    const radio = screen.getByRole('radio', { name: 'Select Suddenly You (2001), Paperback' });
    expect(radio.props.accessibilityState).toMatchObject({ disabled: true });
    await fireEvent.press(radio);

    expect(onPress).not.toHaveBeenCalled();
    expect(calls).toEqual([]);
  });
});

describe('yearSpanOf', () => {
  it('spans the earliest to the latest edition year', () => {
    expect(yearSpanOf([release({ year: 2007 }), release({ year: 1979 }), release({ year: 1999 })])).toBe('1979–2007');
  });

  it('names the single year when every edition shares it', () => {
    expect(yearSpanOf([release({ year: 1979 }), release({ year: 1979 })])).toBe('1979');
  });

  it('skips editions with no year', () => {
    expect(yearSpanOf([release({ year: null }), release({ year: 1979 })])).toBe('1979');
  });

  it('is empty when no edition names a year', () => {
    expect(yearSpanOf([release({ year: null })])).toBe('');
  });
});

describe('WorkRow', () => {
  const alien = () =>
    result(
      [
        release({ id: 'r1', title: 'Alien', format: 'vhs', year: 1979, creators: ['Ridley Scott'] }),
        release({ id: 'r2', title: 'Alien', format: 'dvd', year: 1999, publisher: 'Fox', creators: ['Ridley Scott'] }),
        release({ id: 'r3', title: 'Alien', format: 'bluray', year: 2007, creators: ['Ridley Scott'] }),
      ],
      { kind: 'film', title: 'Alien', originalYear: 1979, creators: ['Ridley Scott'] },
    );

  it('shows the title, creator, year span, and edition count', async () => {
    await render(<WorkRow result={alien()} ownedReleaseIds={new Set()} onPress={jest.fn()} />);

    expect(screen.getByText('Alien')).toBeTruthy();
    expect(screen.getByText('Ridley Scott · 1979–2007 · 3 editions')).toBeTruthy();
  });

  it('counts one edition in the singular', async () => {
    const single = result([release({ year: 2001 })]);
    await render(<WorkRow result={single} ownedReleaseIds={new Set()} onPress={jest.fn()} />);

    expect(screen.getByText('Lisa Kleypas · 2001 · 1 edition')).toBeTruthy();
  });

  it('shows the owned edition cover first', async () => {
    const r = alien();
    r.releases[0].coverUrl = 'https://example.invalid/vhs.jpg';
    r.releases[2].coverUrl = 'https://example.invalid/bluray.jpg';
    await render(<WorkRow result={r} ownedReleaseIds={new Set(['r3'])} onPress={jest.fn()} />);

    expect(screen.getByLabelText('Cover of Alien').props.source).toEqual({ uri: 'https://example.invalid/bluray.jpg' });
  });

  it('falls back to the first edition with art when nothing is owned', async () => {
    const r = alien();
    r.releases[1].coverUrl = 'https://example.invalid/dvd.jpg';
    await render(<WorkRow result={r} ownedReleaseIds={new Set()} onPress={jest.fn()} />);

    expect(screen.getByLabelText('Cover of Alien').props.source).toEqual({ uri: 'https://example.invalid/dvd.jpg' });
  });

  it('falls back to the work art when no edition has any', async () => {
    const r = alien();
    r.work.coverUrl = 'https://example.invalid/work.jpg';
    await render(<WorkRow result={r} ownedReleaseIds={new Set()} onPress={jest.fn()} />);

    expect(screen.getByLabelText('Cover of Alien').props.source).toEqual({ uri: 'https://example.invalid/work.jpg' });
  });

  it('falls back to a plain tile, never a text tile, when nothing has art', async () => {
    await render(<WorkRow result={alien()} ownedReleaseIds={new Set()} onPress={jest.fn()} />);

    expect(screen.queryByLabelText(/Cover of /)).toBeNull();
  });

  it('badges the row when any edition is owned', async () => {
    await render(<WorkRow result={alien()} ownedReleaseIds={new Set(['r2'])} onPress={jest.fn()} />);

    expect(screen.getByText('In your library')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Alien, Ridley Scott · 1979–2007 · 3 editions, in your library' })).toBeTruthy();
  });

  it('shows no badge when nothing is owned', async () => {
    await render(<WorkRow result={alien()} ownedReleaseIds={new Set()} onPress={jest.fn()} />);

    expect(screen.queryByText('In your library')).toBeNull();
    expect(screen.getByRole('button', { name: 'Alien, Ridley Scott · 1979–2007 · 3 editions' })).toBeTruthy();
  });

  it('taps lightly and opens the work on press', async () => {
    const calls = fakeHaptics();
    const onPress = jest.fn();
    await render(<WorkRow result={alien()} ownedReleaseIds={new Set()} onPress={onPress} />);

    await fireEvent.press(screen.getByRole('button', { name: 'Alien, Ridley Scott · 1979–2007 · 3 editions' }));

    expect(onPress).toHaveBeenCalledTimes(1);
    expect(calls).toEqual(['tap']);
  });
});
