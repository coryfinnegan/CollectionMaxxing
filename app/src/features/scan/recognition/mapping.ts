// The one table from the model's words to the server's words, and the shape /identify takes.
// Only text and fields go out: no order, no picture. A box goes out as a position (`toItemBox`), never as a crop.
import type { IdentifyItem, ItemBox, ItemCompleteness, ReleaseFormat, WorkKind } from '@/api/generated/models';
import type { ModelCompleteness, ModelKind, Rect } from '@/native/cm-vision';
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

const unit = (value: number) => Math.min(1, Math.max(0, value));

/**
 * Where a read sits in the picture, as the server takes it (normalized 0..1, origin top-left): the server keeps two reads of one release
 * in different places as two items. Each value is pulled into 0..1, since Vision's boxes overshoot the picture by a hair. A box with no
 * area, a value that is not a number, or none of it inside the picture is left out: a box is a hint, never a reason to lose a read.
 */
export function toItemBox(box: Rect | undefined): ItemBox | undefined {
  if (!box) return undefined;
  const { x, y, width, height } = box;
  if (![x, y, width, height].every(Number.isFinite) || width <= 0 || height <= 0) return undefined;
  if (x >= 1 || y >= 1 || x + width <= 0 || y + height <= 0) return undefined;
  return { x: unit(x), y: unit(y), width: unit(width), height: unit(height) };
}

/** The fields of `IdentifyItem` a read's words fill. Its position is added where the request is made (pipeline/payload.ts). */
export function toIdentifyItem(item: ReadItem): IdentifyItem {
  const out: IdentifyItem = { localId: item.localId, text: item.text, title: item.title, kind: item.kind };
  if (item.creator) out.creator = item.creator;
  if (item.edition) out.edition = item.edition;
  if (item.completeness) out.completeness = item.completeness;
  if (item.platform) out.platform = item.platform;
  if (item.productCode) out.productCode = item.productCode;
  if (item.unconfirmed) out.unconfirmed = true;
  return out;
}
