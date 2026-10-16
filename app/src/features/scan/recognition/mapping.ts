// The one table from the model's words to the server's words, and the shape /identify takes.
// Only text and fields go out: no box, no order, no picture.
import type { IdentifyItem, ItemCompleteness, ReleaseFormat, WorkKind } from '@/api/generated/models';
import type { ModelCompleteness, ModelKind } from '@/native/cm-vision';
import type { ReadItem } from './types';

export interface KindMapping {
  kind: WorkKind;
  format?: ReleaseFormat;
}

/** Books and games leave `format` open: the cover does not say hardcover or paperback, and the platform names the game. */
export const KIND_TABLE: Record<ModelKind, KindMapping> = {
  book: { kind: 'book' },
  vhs: { kind: 'film', format: 'vhs' },
  dvd: { kind: 'film', format: 'dvd' },
  bluray: { kind: 'film', format: 'bluray' },
  uhdBluray: { kind: 'film', format: 'uhd_bluray' },
  game: { kind: 'game' },
  other: { kind: 'other' },
};

export const COMPLETENESS_TABLE: Record<ModelCompleteness, ItemCompleteness> = {
  looseCart: 'cart_only',
  discOnly: 'disc_only',
  boxed: 'complete',
  manualVisible: 'complete',
};

const has = <T extends object>(table: T, key: unknown): key is keyof T =>
  typeof key === 'string' && Object.prototype.hasOwnProperty.call(table, key);

/** An unknown kind is `other`, never an error. */
export function mapKind(kind: ModelKind | string | undefined): KindMapping {
  return has(KIND_TABLE, kind) ? KIND_TABLE[kind] : { kind: 'other' };
}

/** An unknown completeness is left out. */
export function mapCompleteness(completeness: ModelCompleteness | string | undefined): ItemCompleteness | undefined {
  return has(COMPLETENESS_TABLE, completeness) ? COMPLETENESS_TABLE[completeness] : undefined;
}

/** Exactly what `IdentifyItem` takes. */
export function toIdentifyItem(item: ReadItem): IdentifyItem {
  const out: IdentifyItem = { localId: item.localId, text: item.text, title: item.title, kind: item.kind };
  if (item.creator) out.creator = item.creator;
  if (item.edition) out.edition = item.edition;
  if (item.completeness) out.completeness = item.completeness;
  if (item.platform) out.platform = item.platform;
  return out;
}
