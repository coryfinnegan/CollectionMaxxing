import { COMPLETENESS_TABLE, KIND_TABLE, mapCompleteness, mapKind, toIdentifyItem, toItemBox } from './mapping';
import type { ReadItem } from './types';

describe('mapping the model words to the server words', () => {
  it('maps every ModelKind to a work kind and, where the cover shows it, a format', () => {
    expect(KIND_TABLE).toEqual({
      book: { kind: 'book' },
      vhs: { kind: 'film', format: 'vhs' },
      dvd: { kind: 'film', format: 'dvd' },
      bluray: { kind: 'film', format: 'bluray' },
      uhdBluray: { kind: 'film', format: 'uhd_bluray' },
      game: { kind: 'game' },
      other: { kind: 'other' },
    });
  });

  it('maps every ModelCompleteness to an item completeness', () => {
    expect(COMPLETENESS_TABLE).toEqual({
      looseCart: 'cart_only',
      discOnly: 'disc_only',
      boxed: 'complete',
      manualVisible: 'complete',
    });
  });

  it('maps an unknown kind to other and an unknown completeness to nothing, without throwing', () => {
    expect(mapKind('hologram')).toEqual({ kind: 'other' });
    expect(mapKind(undefined)).toEqual({ kind: 'other' });
    expect(mapKind('constructor')).toEqual({ kind: 'other' });
    expect(mapCompleteness('sealedInShrink')).toBeUndefined();
    expect(mapCompleteness(undefined)).toBeUndefined();
    expect(mapCompleteness('toString')).toBeUndefined();
  });

  it('produces exactly the fields IdentifyItem takes, with nothing else', () => {
    const item: ReadItem = {
      localId: 'a',
      text: 'Super Mario 64',
      title: 'Super Mario 64',
      kind: 'game',
      platform: 'Nintendo 64',
      edition: "Player's Choice",
      completeness: 'cart_only',
      box: { x: 0, y: 0, width: 1, height: 1 },
      order: 3,
      confidence: 'high',
    };
    expect(toIdentifyItem(item)).toEqual({
      localId: 'a',
      text: 'Super Mario 64',
      title: 'Super Mario 64',
      kind: 'game',
      platform: 'Nintendo 64',
      edition: "Player's Choice",
      completeness: 'cart_only',
    });
  });

  it('sends a game\'s product code when the read has one, and nothing when it has none', () => {
    const game: ReadItem = { localId: 'a', text: 'SNS-8C-USA', title: 'Super Mario World', kind: 'game', order: 0, confidence: 'low' };
    expect(toIdentifyItem({ ...game, productCode: 'SNS-8C-USA' })).toEqual({
      localId: 'a',
      text: 'SNS-8C-USA',
      title: 'Super Mario World',
      kind: 'game',
      productCode: 'SNS-8C-USA',
    });
    expect(toIdentifyItem(game)).not.toHaveProperty('productCode');
  });

  it('sends the unconfirmed mark of a read only the picture model made, and nothing for a read without it', () => {
    const book: ReadItem = { localId: 'a', text: 'Ubik', title: 'Ubik', kind: 'book', order: 0, confidence: 'high' };
    expect(toIdentifyItem({ ...book, unconfirmed: true })).toEqual({ localId: 'a', text: 'Ubik', title: 'Ubik', kind: 'book', unconfirmed: true });
    expect(toIdentifyItem(book)).not.toHaveProperty('unconfirmed');
    expect(toIdentifyItem({ ...book, unconfirmed: false })).not.toHaveProperty('unconfirmed');
  });
});

describe('toItemBox: where a read sits, as the server takes it', () => {
  it('passes a box inside the picture through as it is', () => {
    expect(toItemBox({ x: 0.1, y: 0.2, width: 0.3, height: 0.4 })).toEqual({ x: 0.1, y: 0.2, width: 0.3, height: 0.4 });
    expect(toItemBox({ x: 0, y: 0, width: 1, height: 1 })).toEqual({ x: 0, y: 0, width: 1, height: 1 });
  });

  it('pulls a value a hair outside the picture in, as Vision\'s boxes sometimes are', () => {
    expect(toItemBox({ x: -0.004, y: 0.2, width: 1.003, height: 0.4 })).toEqual({ x: 0, y: 0.2, width: 1, height: 0.4 });
    expect(toItemBox({ x: 0.5, y: -0.01, width: 0.5, height: 1.2 })).toEqual({ x: 0.5, y: 0, width: 0.5, height: 1 });
  });

  it.each([
    ['no width', { x: 0.1, y: 0.1, width: 0, height: 0.5 }],
    ['no height', { x: 0.1, y: 0.1, width: 0.5, height: 0 }],
    ['a negative width', { x: 0.1, y: 0.1, width: -0.2, height: 0.5 }],
    ['a negative height', { x: 0.1, y: 0.1, width: 0.5, height: -0.2 }],
    ['an x that is not a number', { x: Number.NaN, y: 0.1, width: 0.5, height: 0.5 }],
    ['a y that is infinite', { x: 0.1, y: Number.POSITIVE_INFINITY, width: 0.5, height: 0.5 }],
    ['a width that is not a number', { x: 0.1, y: 0.1, width: Number.NaN, height: 0.5 }],
    ['a height that is infinite', { x: 0.1, y: 0.1, width: 0.5, height: Number.NEGATIVE_INFINITY }],
  ])('leaves the box out: %s', (_why, box) => {
    expect(toItemBox(box)).toBeUndefined();
  });

  it('leaves the box out when there is none', () => {
    expect(toItemBox(undefined)).toBeUndefined();
  });

  it('leaves the box out when pulling it into the picture would leave nothing of it', () => {
    expect(toItemBox({ x: 0.1, y: 1.4, width: 0.5, height: 0.5 })).toBeUndefined();
    expect(toItemBox({ x: 2, y: 0.1, width: 0.5, height: 0.5 })).toBeUndefined();
  });

  it('does not change the box it is given', () => {
    const box = { x: -0.1, y: 0.2, width: 1.5, height: 0.4 };
    toItemBox(box);
    expect(box).toEqual({ x: -0.1, y: 0.2, width: 1.5, height: 0.4 });
  });
});
