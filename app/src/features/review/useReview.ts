// Review's brain: the session's candidates, the owner's draft choices, the commit (once) and the Undo toast.
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { StoreApi } from 'zustand/vanilla';
import type { CandidateDto, DecideCandidateRequest, ReleaseDto } from '@/api/generated/models';
import { newIdempotencyKey } from '@/features/item/etag';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { loadSessionMemory, resolveReviewDefaults } from '@/features/scan/session/sessionContext';
import { useLibraryRole } from '@/features/scan/session/useScanSession';
import { getTrayStore } from '@/features/scan/tray/storage';
import type { TrayStore } from '@/features/scan/tray/trayStore';
import { haptics } from '@/lib/haptics';
import { ApiError } from '@/lib/http';
import { friendlyError } from '@/lib/problem';
import { commitDraft, loadIntoCards, patchCandidate, searchCandidate, skipCandidate, undoCommit } from './api';
import { canCommit, groupCandidates, groupOf, reviewable } from './grouping';
import { addCount, commitBody, committable, emptyDraft, isChecked, type Draft, type LineOverride, type ReviewStatus } from './plan';

/** How long the "Added 11 items" toast offers Undo (docs/06 M5). */
export const UNDO_MS = 10_000;

/** `sessionId` and `undoKey` are what Undo needs: the session the commit closed, and the key that makes a retried Undo the same request. */
export type Toast = { message: string; sessionId: string; undoKey: string; canUndo: boolean; warning?: string | null };

/** Undo was refused: it is too late (the commit is a couple of minutes old) or an item was changed since. The items stay as they are. */
const UNDO_REFUSED = "Couldn't undo. These items were added a while ago or have changed since, so they stay in your library.";

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
  const byId = useCardsStore((s) => s.byId);
  const [draft, setDraft] = useState<Draft>(emptyDraft);
  const [loading, setLoading] = useState(!!sessionId);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [rowErrors, setRowErrors] = useState<Record<string, string>>({});
  const [rowBusy, setRowBusy] = useState<Record<string, boolean>>({});
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
    loadIntoCards(sessionId)
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

  const commit = async () => {
    if (inFlight.current || !canAdd || !sessionId || count === 0) return; // a second tap while the first is running is ignored
    inFlight.current = true;
    setCommitting(true);
    setCommitError(null);
    const chosen = committable(list, draft);
    // The key is tied to the exact request: the same key with different options is refused by the server.
    const signature = `${chosen.map((c) => `${c.id}:${c.copies}`).join(',')}|${JSON.stringify(commitBody(draft, chosen.map((c) => c.id)))}`;
    if (attempt.current?.signature !== signature) attempt.current = { signature, key: newIdempotencyKey() };
    try {
      const result = await commitDraft(sessionId, list, draft, attempt.current.key);
      attempt.current = null;
      for (const c of chosen) useCardsStore.getState().remove(c.id);
      store.getState().clear();
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
    undoing.current = true;
    setCelebration(null); // undone is not a moment to celebrate
    try {
      await undoCommit(toast.sessionId, toast.undoKey);
      // The commit cleared the tray (and with it the session): Review has the session and its results back now.
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
    isChecked: (c: CandidateDto) => isChecked(draft, c),
    canCheck: (c: CandidateDto) => canAdd && canCommit(c, groupOf(c)),
    toggle: (c: CandidateDto) => setChecked(c.id, !isChecked(draft, c)),
    setAddTo: (locationId: string | null) => update({ addTo: locationId }),
    setStatus: (status: ReviewStatus) => update({ status }),
    override,
    choose: (c: CandidateDto, release: ReleaseDto) => onRow(c.id, () => patchCandidate(requireSession(), c.id, { releaseId: release.id })),
    skip: (c: CandidateDto) => onRow(c.id, () => skipCandidate(requireSession(), c.id)),
    setCopies: (c: CandidateDto, copies: number) => onRow(c.id, () => patchCandidate(requireSession(), c.id, { copies: Math.max(1, copies) })),
    edit: (c: CandidateDto, edits: Pick<DecideCandidateRequest, 'format' | 'platform' | 'edition'>) => onRow(c.id, () => patchCandidate(requireSession(), c.id, edits)),
    search: (c: CandidateDto, text: string) => onRow(c.id, () => searchCandidate(requireSession(), c.id, text)),
    commit,
    undo,
    dismissToast: () => setToast(null),
  };
}
