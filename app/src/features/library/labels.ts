// Plain-word labels for the API's enum values (sentence case, per the copy rules).
import type {
  ItemCompleteness,
  ItemCondition,
  ItemStatus,
  ReleaseFormat,
  WorkKind,
} from '@/api/generated/models';

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
export const statuses = Object.keys(statusLabels) as ItemStatus[];
export const conditions = Object.keys(conditionLabels) as Present<ItemCondition>[];
export const completenesses = Object.keys(completenessLabels) as Present<ItemCompleteness>[];
