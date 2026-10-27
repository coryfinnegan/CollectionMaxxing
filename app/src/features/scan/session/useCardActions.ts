// + Add and Wishlist on a card, with a busy flag and a friendly error per card. On an owned card: Add another
// copy (the same accept the commit path adds), Move it here (a location-only patch, undone from the toast), and
// Skip (the owned default; the card goes away and nothing is added). Move hides without a target location.
import { useQueryClient } from '@tanstack/react-query';
import { useRouter } from 'expo-router';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { getItem } from '@/api/generated/items/items';
import type { CandidateDto } from '@/api/generated/models';
import { breadcrumb } from '@/features/item/breadcrumb';
import { useLocationsList } from '@/features/locations/useLocations';
import { getOwnedCache } from '@/features/offline/ownedCache';
import { patchCandidate, skipCandidate, undoAutoCommit } from '@/features/review/api';
import { boundScanStore, boundTitleOf } from '@/features/review/boundScan';
import { UNDO_MS } from '@/features/review/useReview';
import { friendlyError } from '@/lib/problem';
import { ApiError } from '@/lib/http';
import type { CardActions, MoveTarget, MoveToast } from '../cards/ResultCards';
import { moveCopyTo, refreshAfterMove, resolveOwnedItemId } from '../cards/ownedMove';
import { useAutoCommitUndoStore } from './autoCommitUndo';
import { acceptCandidate, wishlistCandidate } from './sessionApi';

/** Undo was refused: the copy changed or left since the move. It stays where it is. */
const MOVE_UNDO_REFUSED = "Couldn't undo. It changed since, so it stays where it is.";

export function useCardActions({
  libraryId,
  sessionId,
  canAdd,
  moveToLocationId = null,
}: {
  libraryId: string;
  sessionId: string | null;
  canAdd: boolean;
  /** Where "Move it here" puts the copy; null hides Move (in-store, Not placed). */
  moveToLocationId?: string | null;
}) {
  const router = useRouter();
  const queryClient = useQueryClient();
  const { locations } = useLocationsList(libraryId);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<Record<string, boolean>>({});
  const [moveToast, setMoveToast] = useState<MoveToast | null>(null);
  const lastMove = useRef<{ itemId: string; previousLocationId: string | null; targetLocationId: string; target: string } | null>(null);
  const undoing = useRef(false);

  const run = useCallback(async (candidate: CandidateDto, action: () => Promise<void>) => {
    setBusy((b) => ({ ...b, [candidate.id]: true }));
    setErrors(({ [candidate.id]: _cleared, ...rest }) => rest);
    try {
      await action();
    } catch (error) {
      setErrors((e) => ({ ...e, [candidate.id]: friendlyError(error) }));
    } finally {
      setBusy((b) => ({ ...b, [candidate.id]: false }));
    }
  }, []);

  useEffect(() => {
    if (!moveToast) return;
    const timer = setTimeout(() => setMoveToast(null), UNDO_MS);
    return () => clearTimeout(timer);
  }, [moveToast]);

  const [wishlisted, setWishlisted] = useState<Record<string, true>>({});

  // The session spot by name for the toast; "here" when the list cannot say (it is the spot in the session chip).
  const placeOf = useCallback(
    (locationId: string | null): string => {
      if (locationId === null) return 'here';
      const place = locations.length > 0 ? breadcrumb(locations, locationId) : 'here';
      return place === 'Not placed' ? 'here' : place;
    },
    [locations],
  );

  const moveTo: MoveTarget | null = useMemo(
    () => (canAdd && moveToLocationId ? { locationId: moveToLocationId, label: placeOf(moveToLocationId) } : null),
    [canAdd, moveToLocationId, placeOf],
  );

  const move = useCallback(
    (candidate: CandidateDto) => {
      const releaseId = candidate.release?.id;
      if (!sessionId || !moveTo || !releaseId) return;
      const target = moveTo;
      void run(candidate, async () => {
        const localItemId = getOwnedCache(libraryId).state.index.lookup({ releaseId }).owned[0]?.itemId ?? null;
        const itemId = await resolveOwnedItemId(libraryId, releaseId, localItemId);
        if (!itemId) throw new ApiError(404, {});
        const moved = await moveCopyTo(libraryId, itemId, target.locationId);
        lastMove.current = moved.alreadyThere
          ? null
          : { itemId, previousLocationId: moved.previousLocationId, targetLocationId: target.locationId, target: target.label };
        setMoveToast(
          moved.alreadyThere
            ? { message: `Already at ${target.label}.`, canUndo: false }
            : { message: `Moved to ${target.label}.`, canUndo: true },
        );
        // A failed refresh must not hide the move: the periodic sync retries it quietly.
        refreshAfterMove(queryClient, libraryId).catch(() => undefined);
      });
    },
    [sessionId, moveTo, run, libraryId, queryClient],
  );

  const undoMove = useCallback(() => {
    const last = lastMove.current;
    if (!last || undoing.current) return;
    undoing.current = true;
    (async () => {
      // The copy moved again (or left) since: restoring the old spot would fight whoever moved it.
      const current = await getItem(libraryId, last.itemId);
      if (current.status !== 200) throw new ApiError(current.status, {});
      if ((current.data.item.locationId ?? null) !== last.targetLocationId) throw new ApiError(412, {});
      await moveCopyTo(libraryId, last.itemId, last.previousLocationId);
    })()
      .then(() => {
        lastMove.current = null;
        setMoveToast({ message: 'Move undone.', canUndo: false });
        refreshAfterMove(queryClient, libraryId).catch(() => undefined);
      })
      .catch((error: unknown) => {
        if (error instanceof ApiError && (error.status === 412 || error.status === 404)) {
          lastMove.current = null;
          setMoveToast({ message: MOVE_UNDO_REFUSED, canUndo: false });
        } else {
          setMoveToast({ message: `${friendlyError(error)} It is still at ${last.target}.`, canUndo: true });
        }
      })
      .finally(() => {
        undoing.current = false;
      });
  }, [libraryId, queryClient]);

  const actions: CardActions = useMemo(
    () => ({
      canAdd,
      errors,
      busy,
      wishlisted,
      moveTo,
      moveToast,
      onOpen: (c) => router.push({ pathname: '/result/[id]', params: { id: c.id } }),
      onAdd: (c) => {
        if (sessionId) void run(c, () => acceptCandidate(sessionId, c));
      },
      onWishlist: (c) =>
        void run(c, async () => {
          await wishlistCandidate(libraryId, c);
          setWishlisted((w) => ({ ...w, [c.id]: true }));
        }),
      onPick: (c, r) => {
        if (sessionId) {
          void run(c, async () => {
            await patchCandidate(sessionId, c.id, { releaseId: r.id });
          });
        }
      },
      onSkip: (c) => {
        if (sessionId) void run(c, () => skipCandidate(sessionId, c.id));
      },
      onMove: move,
      onUndoMove: undoMove,
      onUndoAutoCommit: (c) => {
        if (sessionId)
          void run(c, () =>
            undoAutoCommit(sessionId, c.id).then((undone) => {
              // The snackbar may still be showing this add: it goes away with it.
              if (useAutoCommitUndoStore.getState().last?.candidateId === undone.id) useAutoCommitUndoStore.getState().setLast(null);
            }),
          );
      },
      onVerifyEdition: (c) => {
        if (!canAdd || !c.createdItemId) return;
        boundScanStore.getState().bind({ libraryId, itemId: c.createdItemId, title: boundTitleOf(c), kind: 'itemBarcode' });
        router.push('/scanner');
      },
    }),
    [canAdd, errors, busy, wishlisted, moveTo, moveToast, router, sessionId, run, libraryId, move, undoMove],
  );
  return actions;
}
