import { boxInPicture, gridRegions, linesInPicture } from '../regions';
import type { Rect, TextLine } from '../types';

const line = (text: string, box: Rect, region?: number): TextLine => ({ text, confidence: 0.9, box, pass: 0, ...(region === undefined ? {} : { region }) });
const centreOf = (r: Rect) => ({ x: r.x + r.width / 2, y: r.y + r.height / 2 });

describe('a box read inside a region lands in the whole picture', () => {
  const topRightQuarter: Rect = { x: 0.5, y: 0, width: 0.5, height: 0.5 };

  it('puts a line at the centre of the top-right tile at the centre of the picture\'s top-right quarter', () => {
    const atCentreOfTile: Rect = { x: 0.4, y: 0.4, width: 0.2, height: 0.2 };

    const placed = boxInPicture(atCentreOfTile, topRightQuarter);

    expect(centreOf(placed).x).toBeCloseTo(0.75);
    expect(centreOf(placed).y).toBeCloseTo(0.25);
    expect(placed.width).toBeCloseTo(0.1);
    expect(placed.height).toBeCloseTo(0.1);
  });

  it('keeps the whole region box as the region itself, and a box at its corner at the region\'s corner', () => {
    const region: Rect = { x: 0.45, y: 0, width: 0.55, height: 0.55 };

    expect(boxInPicture({ x: 0, y: 0, width: 1, height: 1 }, region)).toEqual(region);
    const corner = boxInPicture({ x: 0, y: 0, width: 0.1, height: 0.1 }, region);
    expect([corner.x, corner.y]).toEqual([0.45, 0]);
  });

  it('uses the tile\'s overlap: a line at the centre of an overlapping tile is at that tile\'s centre, not the quarter\'s', () => {
    const [, topRight] = gridRegions(2, 2, 0.1);

    const placed = boxInPicture({ x: 0.45, y: 0.45, width: 0.1, height: 0.1 }, topRight);

    expect(centreOf(placed).x).toBeCloseTo(0.725); // the tile runs 0.45 to 1 across
    expect(centreOf(placed).y).toBeCloseTo(0.275); // and 0 to 0.55 down
  });

  it('places a line in the strip two tiles share at the same spot whichever tile read it', () => {
    const [topLeft, topRight] = gridRegions(2, 2, 0.1);
    // x = 0.5 in the picture: 0.5 / 0.55 across the left tile, and (0.5 - 0.45) / 0.55 across the right one
    const fromLeft = boxInPicture({ x: 0.5 / 0.55, y: 0.2, width: 0.05, height: 0.1 }, topLeft);
    const fromRight = boxInPicture({ x: 0.05 / 0.55, y: 0.2, width: 0.05, height: 0.1 }, topRight);

    expect(fromLeft.x).toBeCloseTo(0.5);
    expect(fromRight.x).toBeCloseTo(0.5);
    expect(fromLeft.y).toBeCloseTo(fromRight.y);
  });
});

describe('linesInPicture', () => {
  const regions: Rect[] = [
    { x: 0, y: 0, width: 0.5, height: 0.5 },
    { x: 0.5, y: 0, width: 0.5, height: 0.5 },
  ];

  it('maps each line by the region that read it and keeps its text, confidence, pass and region', () => {
    const lines = [line('LEFT', { x: 0, y: 0, width: 1, height: 1 }, 0), line('RIGHT', { x: 0.2, y: 0.4, width: 0.5, height: 0.2 }, 1)];

    const mapped = linesInPicture(lines, regions);

    expect(mapped).toEqual([
      { text: 'LEFT', confidence: 0.9, box: { x: 0, y: 0, width: 0.5, height: 0.5 }, pass: 0, region: 0 },
      { text: 'RIGHT', confidence: 0.9, box: { x: 0.6, y: 0.2, width: 0.25, height: 0.1 }, pass: 0, region: 1 },
    ]);
  });

  it('does not change the lines it is given', () => {
    const lines = [line('LEFT', { x: 0.2, y: 0.2, width: 0.2, height: 0.2 }, 1)];
    const before = JSON.stringify(lines);

    linesInPicture(lines, regions);

    expect(JSON.stringify(lines)).toBe(before);
  });

  it('leaves a line that no region read as it is, and leaves out one that names a region that was not asked for', () => {
    const whole = line('WHOLE', { x: 0.1, y: 0.1, width: 0.2, height: 0.2 });

    expect(linesInPicture([whole, line('LOST', { x: 0, y: 0, width: 1, height: 1 }, 7)], regions)).toEqual([whole]);
  });
});

describe('gridRegions', () => {
  it('cuts a 2 x 2 grid with 10% overlap into four tiles, top row first, each reaching 5% into its neighbours', () => {
    const tiles = gridRegions(2, 2, 0.1);

    expect(tiles).toHaveLength(4);
    const [topLeft, topRight, bottomLeft, bottomRight] = tiles;
    expect(topLeft).toEqual({ x: 0, y: 0, width: expect.closeTo(0.55), height: expect.closeTo(0.55) });
    expect(topRight.x).toBeCloseTo(0.45);
    expect(topRight.y).toBe(0);
    expect(bottomLeft.x).toBe(0);
    expect(bottomLeft.y).toBeCloseTo(0.45);
    expect(bottomRight.x).toBeCloseTo(0.45);
    expect(bottomRight.y).toBeCloseTo(0.45);
  });

  it('keeps every tile inside the picture, and the tiles together cover it', () => {
    for (const tile of gridRegions(2, 2, 0.1)) {
      expect(tile.x).toBeGreaterThanOrEqual(0);
      expect(tile.y).toBeGreaterThanOrEqual(0);
      expect(tile.x + tile.width).toBeLessThanOrEqual(1 + 1e-9);
      expect(tile.y + tile.height).toBeLessThanOrEqual(1 + 1e-9);
    }
    const [topLeft, topRight, , bottomRight] = gridRegions(2, 2, 0.1);
    expect(topRight.x).toBeLessThan(topLeft.x + topLeft.width); // the neighbours overlap
    expect(bottomRight.x + bottomRight.width).toBeCloseTo(1);
    expect(bottomRight.y + bottomRight.height).toBeCloseTo(1);
  });

  it('with no overlap is the plain grid', () => {
    expect(gridRegions(2, 2, 0)).toEqual([
      { x: 0, y: 0, width: 0.5, height: 0.5 },
      { x: 0.5, y: 0, width: 0.5, height: 0.5 },
      { x: 0, y: 0.5, width: 0.5, height: 0.5 },
      { x: 0.5, y: 0.5, width: 0.5, height: 0.5 },
    ]);
  });

  it('is the whole picture for a 1 x 1 grid, however much overlap it is asked for', () => {
    expect(gridRegions(1, 1, 0.3)).toEqual([{ x: 0, y: 0, width: 1, height: 1 }]);
  });
});
