// E2E-only fixture: the scanner's result cards for a release on the wishlist, one you sold, one you own but cannot find, and an
// ordinary owned one, side by side on fixed data, for the visual spec. Reached as /scan-cards, which production builds redirect
// away from; nothing links here. The cards themselves are the real ones: only the candidates and the actions are fixed.
import { onlineManager } from '@tanstack/react-query';
import { useEffect } from 'react';
import type { CandidateDto } from '@/api/generated/models';
import { upsertOwned } from '@/mocks/ownership';
import { Screen, Text } from '@/ui';
import { ResultCards, type CardActions } from './ResultCards';

const nop = () => undefined;

const actions: CardActions = {
  canAdd: true,
  onOpen: nop,
  onAdd: nop,
  onWishlist: nop,
  onSkip: nop,
  onMove: nop,
  onMoveItem: nop,
  onUndoMoveItem: nop,
};

const candidate = (n: number, title: string, over: Partial<CandidateDto>): CandidateDto =>
  ({
    id: `e3000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
    seq: n,
    source: 'barcode',
    rawCode: `97860000000${n}`,
    resolution: 'matched',
    alreadyOwnedCount: 0,
    ownedAt: [],
    decision: 'undecided',
    release: {
      id: `e4000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
      workId: `e5000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
      title,
      format: 'game_cartridge',
      year: 1990 + n,
      platform: { id: 'p', name: 'Nintendo 64', shortName: 'N64', manufacturer: null },
      creators: [],
      coverUrl: null,
    },
    ...over,
  }) as unknown as CandidateDto;

const SOLD_ID = 'e6000000-0000-4000-8000-000000000002';
const cards = [
  candidate(1, 'Metroid Prime', { wishlistItemId: 'e6000000-0000-4000-8000-000000000001' }),
  candidate(2, 'Tetris', { soldItemId: SOLD_ID }),
  candidate(3, 'Pong', { alreadyOwnedCount: 1, ownedAt: ['Game room › Shelf 1'] }),
  candidate(4, 'Super Mario 64', { alreadyOwnedCount: 1, ownedAt: ['Game room › Shelf 1'] }),
];

/** The phone's own copy of the library answers the cards, so the fixture needs no network. */
function seedCopy() {
  const row = (n: number, title: string, status: 'owned' | 'missing') => ({
    itemId: `e6000000-0000-4000-8000-00000000001${n}`,
    deleted: false,
    releaseId: `e4000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
    workId: `e5000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
    title,
    format: 'game_cartridge' as const,
    platform: 'Nintendo 64',
    identifiers: [],
    locationPath: 'Game room › Shelf 1',
    status,
  });
  upsertOwned(row(3, 'Pong', 'missing'));
  upsertOwned(row(4, 'Super Mario 64', 'owned'));
}

export function ScanCardsFixture() {
  useEffect(() => {
    seedCopy();
    onlineManager.setOnline(true);
  }, []);
  return (
    <Screen>
      <Text variant="heading" accessibilityRole="header">
        Result cards
      </Text>
      <ResultCards cards={cards} actions={actions} />
    </Screen>
  );
}
