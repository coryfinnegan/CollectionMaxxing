// Plain-word labels for the API's enum values (sentence case, per the copy rules).
import type {
  ItemCompleteness,
  ItemCondition,
  ItemStatus,
  ReleaseDto,
  ReleaseFormat,
  WorkKind,
} from '@/api/generated/models';
import { BESIDE_COLLECTION, IN_COLLECTION } from './statuses';

/** The platform's name, whichever shape the release arrived in (a full release object or review's flat wire name). */
export function platformNameOf(r: ReleaseDto): string | null {
  if (r.platform && typeof r.platform === 'object') return r.platform.name ?? null;
  const extra = r as unknown as Record<string, unknown>;
  return typeof extra.platformName === 'string' ? extra.platformName : null;
}

export const kindLabels: Record<WorkKind, string> = {
  game: 'Games',
  film: 'Movies',
  tv: 'TV',
  book: 'Books',
  other: 'Other',
};

export const formatLabels: Record<ReleaseFormat, string> = {
  hardcover: 'Hardcover',
  paperback: 'Paperback',
  mass_market: 'Mass market',
  board_book: 'Board book',
  spiral_bound: 'Spiral-bound',
  vhs: 'VHS',
  betamax: 'Betamax',
  laserdisc: 'LaserDisc',
  dvd: 'DVD',
  bluray: 'Blu-ray',
  uhd_bluray: '4K Blu-ray',
  game_cartridge: 'Cartridge',
  game_disc: 'Game disc',
  game_card: 'Game card',
  other: 'Other',
  unknown: 'Unknown',
};

export const statusLabels: Record<ItemStatus, string> = {
  owned: 'Owned',
  wishlist: 'Wishlist',
  lent: 'Lent out',
  sold: 'Sold',
  missing: 'Missing',
};

type Present<T> = Exclude<T, null>;

export const conditionLabels: Record<Present<ItemCondition>, string> = {
  sealed: 'Sealed',
  mint: 'Mint',
  very_good: 'Very good',
  good: 'Good',
  fair: 'Fair',
  poor: 'Poor',
};

export const completenessLabels: Record<Present<ItemCompleteness>, string> = {
  complete: 'Complete',
  disc_only: 'Disc only',
  cart_only: 'Cartridge only',
  box_only: 'Box only',
  no_manual: 'No manual',
  n_a: 'Not applicable',
};

export const kinds = Object.keys(kindLabels) as WorkKind[];
export const formats = Object.keys(formatLabels) as ReleaseFormat[];
/** Every status, the library's three first and wishlist and sold after them (see statuses.ts). */
export const statuses: ItemStatus[] = [...IN_COLLECTION, ...BESIDE_COLLECTION];
export const conditions = Object.keys(conditionLabels) as Present<ItemCondition>[];
export const completenesses = Object.keys(completenessLabels) as Present<ItemCompleteness>[];
