// Which items each Mall room shows, and how they deal onto its anchors (#357).
// Rooms filter the grid order by kind; anchors take consecutive runs of that
// order, so VoiceOver meets every item in the same place in every mode.
import type { ReactElement } from 'react';
import type { ItemSummaryDto, WorkKind } from '@/api/generated/models';
import { parseAnchorName, roomAnchors, type MallRoomId } from './rooms';

/** The list both Mall views (native 2.5D and web 3D) present. */
export type MallListProps = {
  header: ReactElement;
  empty: ReactElement;
  items: ItemSummaryDto[];
  refreshing: boolean;
  onRefresh: () => void;
  hasNextPage: boolean;
  fetchNextPage: () => void;
  isFetchingNextPage: boolean;
  onOpenItem: (id: string) => void;
};

/** The kinds each room shelves; the hub shows the whole collection. */
export const roomKinds: Record<MallRoomId, WorkKind[] | 'all'> = {
  mall_hub: 'all',
  video_store: ['film', 'tv'],
  toy_store: ['game'],
  bookstore: ['book'],
};

/** The room tabs, in tab order. */
export const mallRoomTabs: MallRoomId[] = ['mall_hub', 'video_store', 'toy_store', 'bookstore'];

/** The tab label per room. */
export function roomLabel(room: MallRoomId): string {
  return room === 'mall_hub' ? 'Mall' : room === 'video_store' ? 'Movies' : room === 'toy_store' ? 'Games' : 'Books';
}

/** The grid order narrowed to one room. */
export function itemsForRoom(room: MallRoomId, items: ItemSummaryDto[]): ItemSummaryDto[] {
  const kinds = roomKinds[room];
  if (kinds === 'all') return items;
  return items.filter((item) => (kinds as WorkKind[]).includes(item.kind));
}

export type AnchorSlice = {
  name: string;
  section: string;
  index: number;
  items: ItemSummaryDto[];
};

/**
 * Covers per anchor in the 3D room. The room windows the collection: each
 * anchor shows one run, and the aisle list below carries every item, so a
 * 500-item library never builds 500 cover meshes.
 */
export const COVERS_PER_ANCHOR = 8;

/** Consecutive runs of the room order, one per anchor node. */
export function anchorSlices(room: MallRoomId, items: ItemSummaryDto[], perAnchor: number = COVERS_PER_ANCHOR): AnchorSlice[] {
  const size = Math.max(Math.floor(perAnchor), 1);
  const roomItems = itemsForRoom(room, items);
  return roomAnchors[room].map((name, anchor) => {
    const parsed = parseAnchorName(name);
    return {
      name,
      section: parsed?.section ?? '',
      index: parsed?.index ?? 0,
      items: roomItems.slice(anchor * size, anchor * size + size),
    };
  });
}

const sectionLabels: Record<string, string> = {
  shelf: 'Shelves',
  new: 'New releases',
  counter: 'Counter',
  wall: 'Game wall',
  case: 'Glass case',
  peg: 'Pegboard',
  stack: 'Stacks',
  mezz: 'Mezzanine',
  cafe: 'Cafe corner',
  table: 'Table',
  store: 'Storefronts',
  kiosk: 'Kiosk',
};

/** The plain-words label for an anchor section. */
export function sectionLabel(section: string): string {
  return sectionLabels[section] ?? section;
}

/** The camera preset slugs (hero, wall, case) as plain-words labels. */
export function presetLabel(name: string): string {
  return name.length === 0 ? name : name.charAt(0).toUpperCase() + name.slice(1);
}
