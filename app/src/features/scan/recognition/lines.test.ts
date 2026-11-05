import type { TextLine } from '@/native/cm-vision';
import { DEFAULT_LOCAL_TUNING as T } from './defaults';
import { line, RecordingVision, STILL } from './fakes';
import { readLines, tilesTouching, withTiles } from './lines';

/** Six tapes lying flat in one pile: a flat line each, 0.4 wide and 0.04 tall, touching top to bottom. */
const pile = (): TextLine[] => Array.from({ length: 6 }, (_, i) => line(`Tape ${i}`, 0.3, 0.1 + i * 0.07, 0.4, 0.04));
const texts = (lines: TextLine[]) => lines.map((l) => l.text).sort();

function reader(scene: { text?: TextLine[]; text180?: TextLine[]; text90?: TextLine[] }) {
  const vision = new RecordingVision({ [STILL.uri]: scene });
  const orientations: number[][] = [];
  const read = vision.recognizeText.bind(vision);
  vision.recognizeText = (uri, options) => {
    orientations.push(options?.orientations ?? [0]);
    return read(uri, options);
  };
  return { vision, orientations };
}

describe('reading text from a still', () => {
  it('reads upright, then turned 90 and 270, and nothing more on a shot that is not a shelf', async () => {
    const { vision, orientations } = reader({ text: pile() });
    const lines = await readLines(vision, STILL.uri, T);
    expect(orientations).toEqual([[0, 90, 270]]);
    expect(lines).toHaveLength(6);
  });

  it('reads upside down too when a shelf shot shows a pile of items lying flat', async () => {
    const { vision, orientations } = reader({ text: pile(), text180: [line('Upside Down Tape', 0.3, 0.6, 0.4, 0.04)] });
    const lines = await readLines(vision, STILL.uri, T, { shelf: true });
    expect(orientations).toEqual([[0, 90, 270], [180]]);
    expect(texts(lines)).toContain('Upside Down Tape');
    expect(lines).toHaveLength(7);
  });

  it('skips the upside-down pass when the shelf shows no pile, so an upright row costs nothing extra', async () => {
    const spines = Array.from({ length: 8 }, (_, i) => line(`Spine ${i}`, 0.04 + i * 0.1, 0.1, 0.05, 0.6));
    const { vision, orientations } = reader({ text: spines });
    await readLines(vision, STILL.uri, T, { shelf: true });
    expect(orientations).toEqual([[0, 90, 270]]);
  });

  it('keeps what it read when the upside-down pass fails', async () => {
    const { vision } = reader({ text: pile() });
    const read = vision.recognizeText.bind(vision);
    vision.recognizeText = (uri, options) => (options?.orientations?.includes(180) ? Promise.reject(new Error('no pass')) : read(uri, options));
    await expect(readLines(vision, STILL.uri, T, { shelf: true })).resolves.toHaveLength(6);
  });

  it('still returns no lines when Vision throws on the first pass', async () => {
    const { vision } = reader({ text: pile() });
    vision.recognizeText = () => Promise.reject(new Error('boom'));
    await expect(readLines(vision, STILL.uri, T, { shelf: true })).resolves.toEqual([]);
  });

  describe('two reads of one picture at the same time', () => {
    /** A Vision whose native reads stay open until `release`, so a second caller arrives while the first is in flight. */
    function held(scene: { text?: TextLine[]; text180?: TextLine[] }) {
      const { vision } = reader(scene);
      const read = vision.recognizeText.bind(vision);
      const calls: number[][] = [];
      let release: () => void = () => {};
      const gate = new Promise<void>((resolve) => (release = resolve));
      vision.recognizeText = async (uri, options) => {
        calls.push(options?.orientations ?? [0]);
        await gate;
        return read(uri, options);
      };
      return { vision, calls, release };
    }

    it('share one native read', async () => {
      const { vision, calls, release } = held({ text: pile() });
      const first = readLines(vision, STILL.uri, T);
      const second = readLines(vision, STILL.uri, T);
      release();
      expect(texts(await first)).toEqual(texts(await second));
      expect(calls).toEqual([[0, 90, 270]]);
    });

    it('do not share a read of another picture', async () => {
      const { vision, calls, release } = held({ text: pile() });
      const first = readLines(vision, STILL.uri, T);
      const second = readLines(vision, 'fixture://other', T);
      release();
      await Promise.all([first, second]);
      expect(calls).toHaveLength(2);
    });

    it('do not share a read between two engines', async () => {
      const a = held({ text: pile() });
      const b = held({ text: pile() });
      const first = readLines(a.vision, STILL.uri, T);
      const second = readLines(b.vision, STILL.uri, T);
      a.release();
      b.release();
      await Promise.all([first, second]);
      expect(a.calls).toHaveLength(1);
      expect(b.calls).toHaveLength(1);
    });

    it('give each caller its own list', async () => {
      const { vision, release } = held({ text: pile() });
      const first = readLines(vision, STILL.uri, T);
      const second = readLines(vision, STILL.uri, T);
      release();
      const [a, b] = [await first, await second];
      expect(a).not.toBe(b);
      a.pop();
      expect(b).toHaveLength(6);
    });

    it('give every caller no lines when the shared read fails', async () => {
      const { vision } = reader({ text: pile() });
      let calls = 0;
      vision.recognizeText = async () => {
        calls += 1;
        throw new Error('boom');
      };
      const results = await Promise.all([readLines(vision, STILL.uri, T), readLines(vision, STILL.uri, T)]);
      expect(results).toEqual([[], []]);
      expect(calls).toBe(1);
    });
  });

  it('reads again when asked after the first read settled', async () => {
    const { vision, orientations } = reader({ text: pile() });
    await readLines(vision, STILL.uri, T);
    await readLines(vision, STILL.uri, T);
    expect(orientations).toEqual([[0, 90, 270], [0, 90, 270]]);
  });

  it('reads again after a failed read settled', async () => {
    const { vision } = reader({ text: pile() });
    const read = vision.recognizeText.bind(vision);
    let calls = 0;
    vision.recognizeText = (uri, options) => (++calls === 1 ? Promise.reject(new Error('boom')) : read(uri, options));
    expect(await readLines(vision, STILL.uri, T)).toEqual([]);
    expect(await readLines(vision, STILL.uri, T)).toHaveLength(6);
  });

  it('adds the upside-down pass for a shelf caller only when another caller shared its upright read', async () => {
    const { vision, orientations } = reader({ text: pile(), text180: [line('Upside Down Tape', 0.3, 0.6, 0.4, 0.04)] });
    const [plain, shelf] = await Promise.all([readLines(vision, STILL.uri, T), readLines(vision, STILL.uri, T, { shelf: true })]);
    expect(orientations).toEqual([[0, 90, 270], [180]]);
    expect(plain).toHaveLength(6);
    expect(texts(shelf)).toContain('Upside Down Tape');
  });

  describe('merging the upside-down reading with the upright ones', () => {
    // Tape 3 lies the other way up: read upright it is noise, read at 180 it is a title, in the same box.
    const upsideDownTape = (upright: number, flipped: number) => ({
      text: [...pile().filter((l) => l.text !== 'Tape 3'), line('HEPA DNOTS', 0.3, 0.31, 0.4, 0.04, upright)],
      text180: [line('STONE', 0.3, 0.31, 0.4, 0.04, flipped)],
    });

    it('keeps the surer reading of the same box: the title read at 180 replaces the noise read upright', async () => {
      const { vision } = reader(upsideDownTape(0.4, 0.9));
      const lines = await readLines(vision, STILL.uri, T, { shelf: true });
      expect(texts(lines)).toContain('STONE');
      expect(texts(lines)).not.toContain('HEPA DNOTS');
      expect(lines).toHaveLength(6);
    });

    it('keeps the upright reading when it is the surer, and drops the upside-down one', async () => {
      const { vision } = reader(upsideDownTape(0.9, 0.4));
      const lines = await readLines(vision, STILL.uri, T, { shelf: true });
      expect(texts(lines)).toContain('HEPA DNOTS');
      expect(texts(lines)).not.toContain('STONE');
      expect(lines).toHaveLength(6);
    });

    it('keeps both readings when their boxes are in different places', async () => {
      const { vision } = reader({ text: pile(), text180: [line('Elsewhere', 0.3, 0.9, 0.4, 0.04)] });
      expect(await readLines(vision, STILL.uri, T, { shelf: true })).toHaveLength(7);
    });

    it('does not take a small line inside a big one for the same box', async () => {
      const { vision } = reader({ text: pile(), text180: [line('tiny', 0.4, 0.11, 0.05, 0.01, 0.95)] });
      const lines = await readLines(vision, STILL.uri, T, { shelf: true });
      expect(texts(lines)).toContain('tiny');
      expect(texts(lines)).toContain('Tape 0');
    });

    it('drops an upside-down reading under the confidence floor like any other line', async () => {
      const { vision } = reader({ text: pile(), text180: [line('Noise', 0.3, 0.9, 0.4, 0.04, 0.1)] });
      expect(texts(await readLines(vision, STILL.uri, T, { shelf: true }))).not.toContain('Noise');
    });
  });
});

describe('adding what a tile read found', () => {
  /** A spine read sideways: tall, 0.018 wide. */
  const spine = line('ELSIE SILVER', 0.525, 0.259, 0.018, 0.111);
  const tile = (text: string, x: number, y: number, w: number, h: number) => ({ ...line(text, x, y, w, h), region: 0 });

  it('keeps a tile line in a new place on a spine', () => {
    const added = tile('OFF TO', 0.525, 0.224, 0.02, 0.012);
    expect(withTiles([spine], [added], T)).toContain(added);
  });

  it.each([
    ['in a box a pass already read', tile('ELSIE SILVER', 0.526, 0.26, 0.018, 0.11)],
    ['tall', tile('OFF TO THE RACES', 0.53, 0.224, 0.012, 0.05)],
    ['of one letter', tile('O', 0.525, 0.224, 0.02, 0.012)],
    ['outside every spine column', tile('OFF TO', 0.8, 0.224, 0.02, 0.012)],
    ['further than three letter widths past the spine\'s end', tile('OFF TO', 0.525, 0.1, 0.02, 0.012)],
  ])('drops a tile line %s', (_name, dropped) => {
    expect(withTiles([spine], [dropped], T)).toEqual([spine]);
  });

  it('asks for the tiles that boxes touch and no others', () => {
    const touched = tilesTouching([{ x: 0.6, y: 0.2, width: 0.05, height: 0.1 }], T);
    expect(touched).toHaveLength(1);
    expect(touched[0].x).toBeGreaterThan(0.4); // the top right tile
    expect(touched[0].y).toBe(0);
  });
});
