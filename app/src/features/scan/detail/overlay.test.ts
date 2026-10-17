import { boxOverlay, fitImage } from './overlay';

describe('where the picture sits in its frame', () => {
  it('fills the width of a frame that is taller than the picture', () => {
    expect(fitImage({ width: 400, height: 600 }, { width: 2000, height: 1000 })).toEqual({ left: 0, top: 200, width: 400, height: 200 });
  });

  it('fills the height of a frame that is wider than the picture', () => {
    expect(fitImage({ width: 600, height: 400 }, { width: 1000, height: 2000 })).toEqual({ left: 200, top: 0, width: 200, height: 400 });
  });

  it('is the whole frame when the shapes match', () => {
    expect(fitImage({ width: 300, height: 200 }, { width: 3000, height: 2000 })).toEqual({ left: 0, top: 0, width: 300, height: 200 });
  });
});

describe('the box on the displayed picture', () => {
  const box = { x: 0.1, y: 0.2, width: 0.5, height: 0.25 };

  it('maps a landscape picture in a portrait frame', () => {
    const r = boxOverlay(box, { width: 400, height: 600 }, { width: 2000, height: 1000 });
    expect(r).toEqual({ left: 40, top: 240, width: 200, height: 50 });
  });

  it('maps a portrait picture in a landscape frame', () => {
    const r = boxOverlay(box, { width: 600, height: 400 }, { width: 1000, height: 2000 });
    expect(r).toEqual({ left: 220, top: 80, width: 100, height: 100 });
  });

  it('maps a square picture in a square frame', () => {
    const r = boxOverlay(box, { width: 200, height: 200 }, { width: 800, height: 800 });
    expect(r).toEqual({ left: 20, top: 40, width: 100, height: 50 });
  });

  it('keeps a box that touches an edge inside the picture', () => {
    const r = boxOverlay({ x: 0.9, y: -0.1, width: 0.3, height: 0.3 }, { width: 100, height: 100 }, { width: 100, height: 100 });
    expect(r).toEqual({ left: 90, top: 0, width: 10, height: 20 });
  });

  it('draws nothing without a box, with an empty box, or before the frame is measured', () => {
    expect(boxOverlay(undefined, { width: 400, height: 600 }, { width: 2000, height: 1000 })).toBeUndefined();
    expect(boxOverlay({ x: 0.2, y: 0.2, width: 0, height: 0.1 }, { width: 400, height: 600 }, { width: 2000, height: 1000 })).toBeUndefined();
    expect(boxOverlay(box, { width: 0, height: 0 }, { width: 2000, height: 1000 })).toBeUndefined();
    expect(boxOverlay(box, { width: 400, height: 600 }, { width: 0, height: 0 })).toBeUndefined();
  });
});
