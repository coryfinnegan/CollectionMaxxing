// Review's brain: the session's candidates, the owner's draft choices, the commit (once) and the Undo toast.
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { StoreApi } from 'zustand/vanilla';
import type { CandidateDto, ReleaseDto } from '@/api/generated/models';
import { newIdempotencyKey } from '@/features/item/etag';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { useLibraryRole } from '@/features/scan/session/useScanSession';
import { getTrayStore } from '@/features/scan/tray/storage';
import type { TrayStore } from '@/features/scan/tray/trayStore';
import { friendlyError } from '@/lib/problem';
import { commitDraft, loadIntoCards, patchCandidate, searchCandidate, skipCandidate, undoCommit } from './api';
import { canCommit, groupCandidates, groupOf, reviewable } from './grouping';
import { addCount, committable, emptyDraft, isChecked, type Draft, type LineOverride, type ReviewStatus } from './plan';

/** How long the "Added 11 items" toast offers Undo (docs/06 M5). */
export const UNDO_MS = 10_000;

export type Toast = { message: string; itemIds: string[]; canUndo: boolean; warning?: string | null };

export function useReview({
  libraryId,
  sessionId,
  store = getTrayStore(),
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
  const inFlight = useRef(false);
  const attempt = useRef<{ signature: string; key: string } | null>(null);

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
      .catch((e) => alive && setLoadError(`${friendlyError(e)} What you scanned is still saved.`))
      .finally(() => alive && setLoading(false));
    return () => {
      alive = false;
    };
  }, [sessionId]);

  useEffect(() => {
    if (!toast) return;
    const timer = setTimeout(() => setToast(null), undoMs);
    return () => clearTimeout(timer);
  }, [toast, undoMs]);

  const update = (patch: Partial<Draft>) => setDraft((d) => ({ ...d, ...patch }));
  const override = useCallback(
    (id: string, change: LineOverride) => setDraft((d) => ({ ...d, overrides: { ...d.overrides, [id]: { ...d.overrides[id], ...change } } })),
    [],
  );
  const setChecked = useCallback((id: string, checked: boolean) => setDraft((d) => ({ ...d, checked: { ...d.checked, [id]: checked } })), []);

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
    const signature = chosen.map((c) => `${c.id}:${c.copies}`).join(',');
    if (attempt.current?.signature !== signature) attempt.current = { signature, key: newIdempotencyKey() };
    try {
      const { result, warning } = await commitDraft(sessionId, libraryId, list, draft, attempt.current.key);
      attempt.current = null;
      for (const c of chosen) useCardsStore.getState().remove(c.id);
      store.getState().clear();
      setDraft(emptyDraft());
      setToast({ message: `Added ${result.created} ${result.created === 1 ? 'item' : 'items'}`, itemIds: result.itemIds, canUndo: true, warning });
    } catch (error) {
      setCommitError(`${friendlyError(error)} Nothing was added, and your picks are still checked.`);
    } finally {
      inFlight.current = false;
      setCommitting(false);
    }
  };

  const undo = async () => {
    if (!toast?.canUndo) return;
    try {
      await undoCommit(libraryId, toast.itemIds);
      setToast({ message: 'Undone. Those items are in Recently deleted.', itemIds: [], canUndo: false });
    } catch (error) {
      setToast({ ...toast, message: friendlyError(error), canUndo: true });
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
    count,
    committing,
    commitError,
    toast,
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
    search: (c: CandidateDto, text: string) => onRow(c.id, () => searchCandidate(requireSession(), c.id, text)),
    commit,
    undo,
    dismissToast: () => setToast(null),
  };
}
