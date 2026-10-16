import { COMPLETENESS_TABLE, KIND_TABLE, mapCompleteness, mapKind, toIdentifyItem } from './mapping';
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
});
