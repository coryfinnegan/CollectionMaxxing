// What the receipt shows for pictures, as plain data: one line per item being read or identified, and the counts the status
// bar uses. IDENTIFYING until the server has named it; QUEUED while the phone cannot reach the server.
import type { CandidateDto } from '@/api/generated/models';
import type { PicItem, PicSnap } from './pipelineStore';

export type PictureStatus = 'identifying' | 'queued' | 'done';

export interface PictureLine {
  key: string;
  snapId: string;
  /** Absent for the snap's own line, shown from the shutter until the first item is read. */
  localId?: string;
  thumbUri: string | undefined;
  title: string;
  status: PictureStatus;
  createdAt: number;
}

/** The card the server made for this item, found by its candidate id or by the id the phone gave the item. */
export function cardFor(item: PicItem, cards: CandidateDto[]): CandidateDto | undefined {
  return cards.find((c) => c.id === item.candidateId) ?? cards.find((c) => !!c.localId && c.localId === item.localId);
}

export function statusOf(snap: PicSnap, card: CandidateDto | undefined, online: boolean): PictureStatus {
  if (card) return card.resolution === 'pending' ? 'identifying' : 'done';
  if (snap.phase === 'reading' || snap.sent) return 'identifying';
  return online ? 'identifying' : 'queued';
}

export function pictureLines(snaps: PicSnap[], cards: CandidateDto[], online: boolean): PictureLine[] {
  const lines: PictureLine[] = [];
  for (const snap of snaps) {
    if (snap.items.length === 0) {
      lines.push({ key: snap.snapId, snapId: snap.snapId, thumbUri: snap.thumbUri, title: 'Picture', status: 'identifying', createdAt: snap.createdAt });
      continue;
    }
    for (const item of snap.items) {
      const card = cardFor(item, cards);
      lines.push({
        key: `${snap.snapId}:${item.localId}`,
        snapId: snap.snapId,
        localId: item.localId,
        thumbUri: item.thumbUri ?? snap.thumbUri,
        title: card?.release?.title ?? (item.title || item.text),
        status: statusOf(snap, card, online),
        createdAt: snap.createdAt,
      });
    }
  }
  return lines;
}

/** Items read from pictures, and how many of them (and of pictures still being read) are not settled yet. */
export function pictureCounts(lines: PictureLine[]): { items: number; processing: number } {
  return {
    items: lines.filter((l) => l.localId !== undefined).length,
    processing: lines.filter((l) => l.status !== 'done').length,
  };
}
