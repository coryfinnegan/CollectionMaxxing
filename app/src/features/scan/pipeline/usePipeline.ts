// Runs the picture pipeline for the scanner screen: starts it, keeps sending what is waiting (a snap read offline goes out when
// the connection returns), tells it whether a barcode is in view, and hands the screen the receipt lines and the counts.
import { useCallback, useEffect, useMemo, useRef } from 'react';
import { useStore } from 'zustand';
import type { StoreApi } from 'zustand/vanilla';
import type { SnapEvent } from '../autosnap/useAutoSnap';
import { useCardsStore, withLocalCards } from '../cards/cardsStore';
import type { ScannerSource } from '../scanner/ScannerSource';
import { getPipelineStore } from './pipelineStore';
import { getTrayStore, useTrayValue } from '../tray/storage';
import type { TrayStore } from '../tray/trayStore';
import { devFixtureReader, DEV_FIXTURE_PHOTO } from './devFixtures';
import { pictureCounts, pictureLines } from './lines';
import { SnapPipeline, type PipelineDeps, type Reader } from './SnapPipeline';

/** A barcode seen this recently still counts as in view (the same memory auto-snap uses). */
const BARCODE_MEMORY_MS = 600;

export function usePipeline({
  deps,
  source,
  resendMs = 5000,
  onOutcome,
  boundCover,
}: {
  deps: PipelineDeps | undefined;
  source: ScannerSource;
  resendMs?: number;
  /** Whether the last picture found anything. */
  onOutcome?: (found: boolean) => void;
  /** Review's "Snap cover": every picture from this screen upgrades this row in place. */
  boundCover?: { candidateId: string; onDone(): void };
}) {
  const pipeline = useMemo(() => (deps ? new SnapPipeline(deps) : undefined), [deps]);
  const store = deps?.store ?? getPipelineStore();
  const tray: StoreApi<TrayStore> | undefined = deps ? (deps.tray ?? getTrayStore(deps.libraryId)) : undefined;
  const lastBarcodeAt = useRef(-Infinity);
  const outcome = useRef(onOutcome);
  useEffect(() => {
    outcome.current = onOutcome;
  });

  useEffect(() => {
    if (!pipeline) return;
    pipeline.setBarcodeProbe(() => Date.now() - lastBarcodeAt.current < BARCODE_MEMORY_MS);
    pipeline.setOutcomeHandler((found) => outcome.current?.(found));
    const off = source.start((codes) => {
      if (codes.length > 0) lastBarcodeAt.current = Date.now();
    });
    return off;
  }, [pipeline, source]);

  useEffect(() => {
    if (!pipeline) return;
    pipeline.start();
    void pipeline.flush();
    const timer = setInterval(() => void pipeline.flush(), resendMs);
    return () => {
      clearInterval(timer);
      pipeline.stop();
    };
  }, [pipeline, resendMs]);

  // A committed or discarded scan leaves the receipt; a (re)opened session drains what is waiting, barcodes first.
  const sessionId = useTrayValue(tray, (s) => s.sessionId, null);
  useEffect(() => {
    pipeline?.sweep();
    void pipeline?.flush();
  }, [pipeline, sessionId]);

  const snaps = useStore(store, (s) => s.snaps);
  const unreadableCount = useStore(store, (s) => s.unreadableCount);
  const orphanedCount = useStore(store, (s) => s.orphanedCount);
  const online = useTrayValue(tray, (s) => s.online, true);
  const byId = useCardsStore((s) => s.byId);
  const local = useCardsStore((s) => s.local);
  const cards = useMemo(() => withLocalCards({ byId, local }), [byId, local]);
  // One store holds every library's snaps; the receipt shows only this scanner's own.
  const mine = useMemo(() => (deps ? snaps.filter((s) => s.libraryId === deps.libraryId) : snaps), [deps, snaps]);
  const lines = useMemo(() => pictureLines(mine, cards, online), [mine, cards, online]);
  const counts = useMemo(() => pictureCounts(lines), [lines]);

  const onSnap = useCallback(
    (event: SnapEvent) => void pipeline?.onSnap(event, boundCover ? { bound: boundCover } : undefined),
    [pipeline, boundCover],
  );
  /** One picture with a reader of the caller's choosing (Import from Photos watches what the read gave). */
  const snapWith = useCallback(
    (event: SnapEvent, options: { recognition: Reader }) => pipeline?.onSnap(event, options) ?? Promise.resolve(),
    [pipeline],
  );
  const replayFixture = useCallback(
    () => void pipeline?.onSnap({ photo: DEV_FIXTURE_PHOTO, trigger: 'shutter', shotKind: 'shelf_segment' }, { recognition: devFixtureReader() }),
    [pipeline],
  );

  return {
    enabled: !!pipeline,
    onSnap,
    snapWith,
    recognition: deps?.recognition,
    replayFixture,
    lines,
    items: counts.items,
    processing: counts.processing,
    unreadableCount,
    dismissUnreadable: () => store.getState().dismissUnreadable(),
    orphanedCount,
    dismissOrphaned: () => store.getState().dismissOrphaned(),
    removeLine: (snapId: string, localId: string) => pipeline?.removeItem(snapId, localId),
  };
}
