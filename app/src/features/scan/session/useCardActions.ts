// + Add and Wishlist on a card, with a busy flag and a friendly error per card.
import { useRouter } from 'expo-router';
import { useCallback, useMemo, useState } from 'react';
import type { CandidateDto } from '@/api/generated/models';
import { friendlyError } from '@/lib/problem';
import type { CardActions } from '../cards/ResultCards';
import { acceptCandidate, wishlistCandidate } from './sessionApi';

export function useCardActions({ libraryId, sessionId, canAdd }: { libraryId: string; sessionId: string | null; canAdd: boolean }) {
  const router = useRouter();
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<Record<string, boolean>>({});

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

  const [wishlisted, setWishlisted] = useState<Record<string, true>>({});

  const actions: CardActions = useMemo(
    () => ({
      canAdd,
      errors,
      busy,
      wishlisted,
      onOpen: (c) => router.push({ pathname: '/result/[id]', params: { id: c.id } }),
      onAdd: (c) => {
        if (sessionId) void run(c, () => acceptCandidate(sessionId, c));
      },
      onWishlist: (c) =>
        void run(c, async () => {
          await wishlistCandidate(libraryId, c);
          setWishlisted((w) => ({ ...w, [c.id]: true }));
        }),
    }),
    [canAdd, errors, busy, wishlisted, router, sessionId, run, libraryId],
  );
  return actions;
}
