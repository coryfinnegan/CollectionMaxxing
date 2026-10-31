// Review's brain: the session's candidates, the owner's draft choices, the commit (once) and the Undo toast.
import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { StoreApi } from 'zustand/vanilla';
import type { CandidateDto, DecideCandidateRequest, ReleaseDto } from '@/api/generated/models';
import { newIdempotencyKey } from '@/features/item/etag';
import { invalidateLibraryItems } from '@/features/item/useItem';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { syncLibrary } from '@/features/offline/sync';
import { flushDiscardOutbox } from '@/features/scan/session/discards';
import { loadSessionMemory, resolveReviewDefaults } from '@/features/scan/session/sessionContext';
import { useLibraryRole } from '@/features/scan/session/useScanSession';
import { getTrayStore } from '@/features/scan/tray/storage';
import type { TrayStore } from '@/features/scan/tray/trayStore';
import { haptics } from '@/lib/haptics';
import { ApiError } from '@/lib/http';
import { friendlyError } from '@/lib/problem';
import { commitDraft, discardCandidates, loadIntoCards, patchCandidate, searchCandidate, skipCandidate, undoCommit } from './api';
import { canCommit, groupCandidates, groupOf, reviewable } from './grouping';
import { flushProvisionalOutbox, keepAsUnverified } from './provisionals';
import { contextWorkIdsFor } from './searchContext';
import { addCount, commitBody, committable, emptyDraft, isChecked, picked, selectedPick, type Draft, type LineOverride, type ReviewStatus } from './plan';

/** How long the "Added 11 items" toast offers Undo (docs/06 M5). */
export const UNDO_MS = 10_000;

/** `sessionId` and `undoKey` are what Undo needs: the session the commit closed, and the key that makes a retried Undo the same request. */
export type Toast = { message: string; sessionId: string; undoKey: string; canUndo: boolean; warning?: string | null };

/** Undo was refused: it is too late (the commit is a couple of minutes old) or an item was changed since. The items stay as they are. */
const UNDO_REFUSED = "Couldn't undo. These items were added a while ago or have changed since, so they stay in your library.";

/** A new scan has started since the commit, so reopening the old one would lose the new one. The items stay. */
const UNDO_NEW_SCAN = "Couldn't undo because you've started a new scan. The items stay in your library.";

export function useReview({
  libraryId,
  sessionId,
  store = getTrayStore(libraryId),
  undoMs = UNDO_MS,
}: {
  libraryId: string;
  sessionId: string | null;
  store?: StoreApi<TrayStore>;
  undoMs?: number;
}) {
  const { role, canAdd } = useLibraryRole(libraryId);
  const queryClient = useQueryClient();
  const byId = useCardsStore((s) => s.byId);
  const [draft, setDraft] = useState<Draft>(emptyDraft);
  const [loading, setLoading] = useState(!!sessionId);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [rowErrors, setRowErrors] = useState<Record<string, string>>({});
  const [rowBusy, setRowBusy] = useState<Record<string, boolean>>({});
  const [discardingAll, setDiscardingAll] = useState(false);
  /** Rows whose keep is queued offline: still visible T2 rows until the keep lands. */
  const [queued, setQueued] = useState<Record<string, boolean>>({});
  const [committing, setCommitting] = useState(false);
  const [commitError, setCommitError] = useState<string | null>(null);
  const [toast, setToast] = useState<Toast | null>(null);
  const [celebration, setCelebration] = useState<{ key: number; message: string } | null>(null);
  const [inStore, setInStore] = useState(false);
  const pristine = useRef(true); // a choice made before the session answers wins over its defaults
  const inFlight = useRef(false);
  const undoing = useRef(false);
  const attempt = useRef<{ signature: string; key: string } | null>(null);
  const celebrationKey = useRef(0);

  const dismissCelebration = useCallback(() => setCelebration(null), []);

  const list = useMemo(() => reviewable(Object.values(byId)), [byId]);
  const groups = useMemo(() => groupCandidates(list), [list]);
  const count = addCount(list, draft);

  useEffect(() => {
    if (!sessionId) {
      setLoading(false);
      return;
    }
    let alive = true;
    setLoading(true);
    // Queued keeps and queued discards go first (a kill or a dead zone left them waiting), then the fresh snapshot.
    flushProvisionalOutbox({ libraryId, onStale: () => alive && void loadIntoCards(sessionId).catch(() => {}) })
      .catch(() => {})
      .then(() => flushDiscardOutbox({ libraryId }).catch(() => 0))
      .then(() => loadIntoCards(sessionId))
      .then((session) => {
        if (!alive) return;
        setInStore(session.channel === 'in_store');
        if (!pristine.current) return;
        const defaults = resolveReviewDefaults(session, loadSessionMemory(libraryId));
        setDraft((d) => ({ ...d, addTo: defaults.addTo, status: defaults.status }));
      })
      .catch((e) => alive && setLoadError(`${friendlyError(e)} What you scanned is still saved.`))
      .finally(() => alive && setLoading(false));
    return () => {
      alive = false;
    };
  }, [sessionId, libraryId]);

  useEffect(() => {
    if (!toast) return;
    const timer = setTimeout(() => setToast(null), undoMs);
    return () => clearTimeout(timer);
  }, [toast, undoMs]);

  const update = (patch: Partial<Draft>) => {
    pristine.current = false;
    setDraft((d) => ({ ...d, ...patch }));
  };
  const override = useCallback((id: string, change: LineOverride) => {
    pristine.current = false;
    setDraft((d) => ({ ...d, overrides: { ...d.overrides, [id]: { ...d.overrides[id], ...change } } }));
  }, []);
  const setChecked = useCallback((id: string, checked: boolean) => {
    pristine.current = false;
    setDraft((d) => ({ ...d, checked: { ...d.checked, [id]: checked } }));
  }, []);

  /** Stages a pick-one tap: radio behavior, so tapping the selected edition again changes nothing. The commit resolves it. */
  const select = useCallback((c: CandidateDto, release: ReleaseDto) => {
    pristine.current = false;
    setDraft((d) => (d.picks[c.id] === release.id ? d : { ...d, picks: { ...d.picks, [c.id]: release.id } }));
  }, []);

  /** Runs one row's server call with a busy flag and its own friendly error. */
  const onRow = useCallback(async (id: string, action: () => Promise<unknown>) => {
    setRowBusy((b) => ({ ...b, [id]: true }));
    setRowErrors(({ [id]: _cleared, ...rest }) => rest);
    try {
      await action();
    } catch (error) {
      setRowErrors((e) => ({ ...e, [id]: friendlyError(error) }));
    } finally {
      setRowBusy((b) => ({ ...b, [id]: false }));
    }
  }, []);

  const requireSession = (): string => {
    if (!sessionId || !canAdd) throw new Error('read-only');
    return sessionId;
  };

  /** Discards the whole Not found group at once: one queueing, one flush, one reseed of the counters. */
  const discardAll = useCallback(
    async (rows: readonly CandidateDto[]) => {
      if (!sessionId || !canAdd || rows.length === 0) return;
      setDiscardingAll(true);
      try {
        await discardCandidates({ libraryId, sessionId, candidates: rows, tray: store });
      } finally {
        setDiscardingAll(false);
      }
    },
    [libraryId, sessionId, canAdd, store],
  );

  /** "Keep as unverified": the row's crop goes up with the keep, and only with the keep. */
  const keep = useCallback(
    async (c: CandidateDto, cropUri?: string) => {
      setRowBusy((b) => ({ ...b, [c.id]: true }));
      setRowErrors(({ [c.id]: _cleared, ...rest }) => rest);
      try {
        const result = await keepAsUnverified({ libraryId, canEdit: canAdd, sessionId, candidate: c, cropUri });
        if (result.ok && result.queued) {
          setQueued((q) => ({ ...q, [c.id]: true }));
        } else if (!result.ok && result.reason === 'stale') {
          if (sessionId) await loadIntoCards(sessionId).catch(() => {}); // already kept: the fresh snapshot drops the row
        } else if (!result.ok && result.reason === 'failed') {
          setRowErrors((e) => ({ ...e, [c.id]: result.message ?? friendlyError(new Error('failed')) }));
        }
      } finally {
        setRowBusy((b) => ({ ...b, [c.id]: false }));
      }
    },
    [libraryId, canAdd, sessionId],
  );

  const commit = async () => {
    if (inFlight.current || !canAdd || !sessionId || count === 0) return; // a second tap while the first is running is ignored
    inFlight.current = true;
    setCommitting(true);
    setCommitError(null);
    // What the phone still owes the server goes first. A keep or a discard queued offline would be refused once the commit
    // closes the session, and a discarded row that was accepted on a scanner card would still be added.
    await flushProvisionalOutbox({ libraryId, onStale: () => void loadIntoCards(sessionId).catch(() => {}) }).catch(() => 0);
    await flushDiscardOutbox({ libraryId }).catch(() => 0);
    // Staged pick-one selections resolve first: each chosen edition PATCHes its row, which lands
    // matched and checked, so the one commit below adds exactly what Add N counted. A pick that
    // cannot be saved stops the whole commit: nothing is added then.
    const flushed: string[] = [];
    for (const c of picked(list, draft)) {
      const option = selectedPick(draft, c);
      if (!option) continue;
      setRowBusy((b) => ({ ...b, [c.id]: true }));
      setRowErrors(({ [c.id]: _cleared, ...rest }) => rest);
      try {
        await patchCandidate(sessionId, c.id, { releaseId: option.id });
        flushed.push(c.id);
        setChecked(c.id, true); // the tap chose this edition: it stays in the commit even when owned
      } catch (error) {
        setRowErrors((e) => ({ ...e, [c.id]: friendlyError(error) }));
        setCommitError('A pick could not be saved. Nothing was added, and your picks are still checked.');
        setRowBusy((b) => ({ ...b, [c.id]: false }));
        inFlight.current = false;
        setCommitting(false);
        return;
      }
      setRowBusy((b) => ({ ...b, [c.id]: false }));
    }
    const settled: Draft = { ...draft, checked: { ...draft.checked, ...Object.fromEntries(flushed.map((id) => [id, true])) } };
    const fresh = reviewable(Object.values(useCardsStore.getState().byId));
    const chosen = committable(fresh, settled);
    // The key is tied to the exact request: the same key with different options is refused by the server.
    const signature = `${chosen.map((c) => `${c.id}:${c.copies}`).join(',')}|${JSON.stringify(commitBody(settled, chosen.map((c) => c.id)))}`;
    if (attempt.current?.signature !== signature) attempt.current = { signature, key: newIdempotencyKey() };
    try {
      const result = await commitDraft(sessionId, fresh, settled, attempt.current.key);
      // The library list shows the new items by itself (#448): no pull-to-refresh.
      await invalidateLibraryItems(queryClient, libraryId);
      void syncLibrary(libraryId); // the offline "do I own this" copy learns the new items now, not at the next sync
      attempt.current = null;
      // The session is closed. Its rows go (the ones not added would otherwise return, ticked, in the next Review and fail
      // there), and so do the tray lines the server has in full. Scans still waiting on the phone stay and start the next session.
      useCardsStore.getState().endSession();
      store.getState().closeSession();
      setDraft(emptyDraft());
      const message = `Added ${result.created} ${result.created === 1 ? 'item' : 'items'}`;
      celebrationKey.current += 1;
      setCelebration({ key: celebrationKey.current, message });
      setToast({
        message,
        sessionId,
        undoKey: newIdempotencyKey(),
        canUndo: true,
      });
      void haptics.succeed();
    } catch (error) {
      setCommitError(`${friendlyError(error)} Nothing was added, and your picks are still checked.`);
    } finally {
      inFlight.current = false;
      setCommitting(false);
    }
  };

  const undo = async () => {
    if (!toast?.canUndo || undoing.current) return;
    // Scans that were waiting on the phone may already have started a new session. Reopening this one would strand them.
    const current = store.getState().sessionId;
    if (current && current !== toast.sessionId) {
      setToast({ ...toast, message: UNDO_NEW_SCAN, canUndo: false });
      return;
    }
    undoing.current = true;
    setCelebration(null); // undone is not a moment to celebrate
    try {
      await undoCommit(toast.sessionId, toast.undoKey);
      // The taken-back items leave the library list by themselves (#448).
      await invalidateLibraryItems(queryClient, libraryId);
      void syncLibrary(libraryId); // the taken-back items leave the offline "do I own this" copy too
      // The commit closed the tray's session: Review has the session and its results back now.
      store.getState().setSessionId(toast.sessionId);
      setDraft(emptyDraft());
      setToast({ ...toast, message: 'Undone. What you scanned is back in Review.', canUndo: false });
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        setToast({ ...toast, message: UNDO_REFUSED, canUndo: false });
      } else {
        setToast({ ...toast, message: `${friendlyError(error)} The items are still in your library.`, canUndo: true });
      }
    } finally {
      undoing.current = false;
    }
  };

  return {
    role,
    canAdd,
    loading,
    loadError,
    list,
    groups,
    draft,
    inStore,
    count,
    committing,
    commitError,
    toast,
    celebration,
    dismissCelebration,
    rowErrors,
    rowBusy,
    discardingAll,
    queued,
    keep,
    discardAll,
    isChecked: (c: CandidateDto) => isChecked(draft, c),
    canCheck: (c: CandidateDto) => canAdd && canCommit(c, groupOf(c)),
    toggle: (c: CandidateDto) => setChecked(c.id, !isChecked(draft, c)),
    pickOf: (c: CandidateDto) => selectedPick(draft, c),
    select,
    setAddTo: (locationId: string | null) => update({ addTo: locationId }),
    setStatus: (status: ReviewStatus) => update({ status }),
    override,
    skip: (c: CandidateDto) => onRow(c.id, () => skipCandidate(requireSession(), c.id)),
    discard: (c: CandidateDto) => onRow(c.id, () => discardCandidates({ libraryId, sessionId: requireSession(), candidates: [c], tray: store })),
    setCopies: (c: CandidateDto, copies: number) => onRow(c.id, () => patchCandidate(requireSession(), c.id, { copies: Math.max(1, copies) })),
    edit: (c: CandidateDto, edits: Pick<DecideCandidateRequest, 'format' | 'platform' | 'edition'>) => onRow(c.id, () => patchCandidate(requireSession(), c.id, edits)),
    search: (c: CandidateDto, text: string) => onRow(c.id, () => searchCandidate(requireSession(), c.id, text, contextWorkIdsFor(c, list))),
    commit,
    undo,
    dismissToast: () => setToast(null),
  };
}
