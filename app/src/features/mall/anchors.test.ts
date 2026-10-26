// The Mall anchor mapping (#357): rooms narrow the grid order, anchors window it.
import type { ItemSummaryDto, WorkKind } from '@/api/generated/models';
import { anchorSlices, COVERS_PER_ANCHOR, itemsForRoom, mallRoomTabs, presetLabel, roomLabel, sectionLabel } from './anchors';
import { roomAnchors } from './rooms';

function makeItem(n: number, kind: WorkKind): ItemSummaryDto {
  const id = `d0000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
  return {
    id,
    releaseId: id,
    workId: id,
    title: `Item ${n}`,
    kind,
    format: 'dvd',
    platform: null,
    year: 2000,
    status: 'owned',
    condition: null,
    locationId: null,
    coverUrl: null,
    createdAt: '2026-09-01T12:00:00Z',
    editionVerified: true,
  };
}

const mixed: ItemSummaryDto[] = [
  makeItem(1, 'film'),
  makeItem(2, 'game'),
  makeItem(3, 'book'),
  makeItem(4, 'tv'),
  makeItem(5, 'other'),
];

describe('itemsForRoom', () => {
  it('shows the whole collection in the hub', () => {
    expect(itemsForRoom('mall_hub', mixed)).toEqual(mixed);
  });

  it('narrows each store to its kinds, keeping the order', () => {
    expect(itemsForRoom('video_store', mixed).map((i) => i.kind)).toEqual(['film', 'tv']);
    expect(itemsForRoom('toy_store', mixed).map((i) => i.kind)).toEqual(['game']);
    expect(itemsForRoom('bookstore', mixed).map((i) => i.kind)).toEqual(['book']);
  });
});

describe('anchorSlices', () => {
  it('deals consecutive runs onto every anchor', () => {
    const items = Array.from({ length: 10 }, (_, n) => makeItem(n + 1, 'book'));

    const slices = anchorSlices('bookstore', items, 4);

    expect(slices.map((s) => s.name)).toEqual(roomAnchors.bookstore);
    expect(slices[0].items.map((i) => i.title)).toEqual(['Item 1', 'Item 2', 'Item 3', 'Item 4']);
    expect(slices[1].items.map((i) => i.title)).toEqual(['Item 5', 'Item 6', 'Item 7', 'Item 8']);
    expect(slices[2].items.map((i) => i.title)).toEqual(['Item 9', 'Item 10']);
    expect(slices[0].section).toBe('stack');
    expect(slices[0].index).toBe(1);
  });

  it('windows large libraries to eight covers per anchor', () => {
    expect(COVERS_PER_ANCHOR).toBe(8);
    const items = Array.from({ length: 500 }, (_, n) => makeItem(n + 1, 'film'));

    const shown = anchorSlices('video_store', items).flatMap((s) => s.items);

    expect(shown).toHaveLength(roomAnchors.video_store.length * COVERS_PER_ANCHOR);
    expect(shown[0].title).toBe('Item 1');
  });
});

describe('the Mall labels', () => {
  it('names the room tabs', () => {
    expect(mallRoomTabs.map(roomLabel)).toEqual(['Mall', 'Movies', 'Games', 'Books']);
  });

  it('names every furnished section in plain words', () => {
    expect(sectionLabel('wall')).toBe('Game wall');
    expect(sectionLabel('new')).toBe('New releases');
    expect(sectionLabel('cafe')).toBe('Cafe corner');
    expect(sectionLabel('kiosk')).toBe('Kiosk');
  });

  it('labels the camera presets', () => {
    expect(presetLabel('hero')).toBe('Hero');
    expect(presetLabel('wall')).toBe('Wall');
  });
});
