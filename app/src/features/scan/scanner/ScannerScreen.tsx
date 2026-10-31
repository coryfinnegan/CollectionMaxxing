// The scanner (docs/06 M3): camera behind everything, "Nice scan!" flash, the count bar. Captures queue
// silently here (#432): no result cards, no snap strip — processing state lives on the Review rows, and a row
// opens the Result screen. It is given its sources, so tests and the dev menu drive it with a FakeScannerSource
// and no camera.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useQueryClient } from '@tanstack/react-query';
import { useRouter } from 'expo-router';
import { useContext, useEffect, useMemo, useRef, useState, type MutableRefObject, type ReactNode } from 'react';
import { Platform, Pressable, StyleSheet, View } from 'react-native';
import { SafeAreaInsetsContext } from 'react-native-safe-area-context';
import { StyleSheet as Unistyles } from 'react-native-unistyles';
import { NiceScanFlash } from '../autosnap/NiceScanFlash';
import { DEFAULT_AUTO_SNAP_CONFIG } from '../autosnap/defaults';
import { createCountingSnapHandler, useAutoSnap, type AutoSnapTuning, type SnapEvent } from '../autosnap/useAutoSnap';
import type { AutoSnapConfig } from '../autosnap/types';
import { submitItemBarcodeUpgrade } from '@/features/item/upgrade';
import { invalidateLibraryItems } from '@/features/item/useItem';
import { submitBoundBarcode } from '@/features/review/api';
import type { BoundScan } from '@/features/review/boundScan';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { deviceBarcodes, devicePicker } from '../import/devicePicker';
import type { BarcodeDetector, ImportDeps, PhotoPicker } from '../import/photoImport';
import { usePhotoImport } from '../import/usePhotoImport';
import type { Resizer } from '../autosnap/snapPhoto';
import type { PipelineDeps } from '../pipeline/SnapPipeline';
import { usePipeline } from '../pipeline/usePipeline';
import { AutoCommitSnackbar } from '../session/AutoCommitSnackbar';
import { useScanSession, type SessionTuning } from '../session/useScanSession';
import { useSessionCounters } from '../session/useSessionCounters';
import { SessionSheet } from '../session/SessionSheet';
import { defaultSessionContext, sessionCreateBody, type SessionContext } from '../session/sessionContext';
import { ReceiptBar } from '../tray/ReceiptBar';
import { lineStatus } from '../tray/tray';
import { fixed, spacing, useColors } from '@/theme/tokens';
import { Button, Chip, Text } from '@/ui';
import { DEV_SIX_CODES } from './devCodes';
import { FakeScannerSource, type ScannerSource } from './ScannerSource';
import { ScannerControls } from './ScannerControls';
import { ScannerSearch } from './ScannerSearch';
import { TransientHint } from './TransientHint';

type Props = {
  libraryId: string;
  source: ScannerSource;
  /** The camera, drawn behind everything. Absent in tests. */
  preview?: ReactNode;
  /** In dev builds only: the fake camera the "Scan these 6 codes" button drives. */
  devSource?: FakeScannerSource;
  tuning?: SessionTuning;
  /** What Close does; defaults to going back. The route passes the close-scanner sheet here. */
  onClose?: () => void;
  /** False when embedded in the Scan tab: the tab bar is the way out, so there is no Close. */
  showClose?: boolean;
  /** Each picture, from the shutter or auto-snap. Without this and without a `pipeline`, pictures are only counted. */
  onSnap?: (event: SnapEvent) => void;
  /** The picture pipeline: read on the phone, identified from text, close calls compared by artwork. The route builds it. */
  pipeline?: PipelineDeps;
  /** The auto-snap thresholds. The route passes the server's values (cached on the phone). */
  autoSnapConfig?: AutoSnapConfig;
  autoSnapTuning?: AutoSnapTuning;
  /** Filled with a function that tells auto-snap whether the last picture found anything (misfires raise the thresholds). */
  outcomeRef?: MutableRefObject<((found: boolean) => void) | null>;
  /** Import from Photos. Tests pass a scripted picker; the phone uses the system one. */
  photoImport?: { picker?: PhotoPicker; vision?: () => BarcodeDetector | Promise<BarcodeDetector>; resize?: Resizer };
  /** Review's "Scan barcode": the camera reads for this row, and leaves once the read is sent. */
  boundScan?: BoundScan & { onDone(): void; onCancel(): void };
  /**
   * Whether this screen is the one being looked at (its tab is selected, the app is in front). The tab bar keeps the Scan tab
   * mounted, so when this is false the scanner reads no barcodes, takes no pictures and retries nothing. Defaults to true.
   */
  active?: boolean;
};

export function ScannerScreen({
  libraryId,
  source,
  preview,
  devSource,
  tuning,
  onClose,
  showClose = true,
  onSnap,
  pipeline,
  autoSnapConfig = DEFAULT_AUTO_SNAP_CONFIG,
  autoSnapTuning,
  outcomeRef,
  photoImport,
  boundScan,
  active = true,
}: Props) {
  const router = useRouter();
  const colors = useColors();
  const insets = useContext(SafeAreaInsetsContext) ?? { top: 0, bottom: 0, left: 0, right: 0 };
  // Barcodes found in imported pictures arrive here and take the same path as the camera's.
  const importCodes = useMemo(() => new FakeScannerSource(), []);
  const sources = useMemo(() => (devSource ? [source, devSource, importCodes] : [source, importCodes]), [source, devSource, importCodes]);
  // What this scan inherits: the library's remembered spot, changed in the sheet, sent when the session is created.
  const [context, setContext] = useState<SessionContext>(() => defaultSessionContext(libraryId));
  const [contextLibraryId, setContextLibraryId] = useState(libraryId);
  if (contextLibraryId !== libraryId) {
    // A switched-to library starts from its own remembered spot. Adjusted during render (not in an effect) so the
    // bar never shows one library's spot while scanning another.
    setContextLibraryId(libraryId);
    setContext(defaultSessionContext(libraryId));
  }
  const [sheet, setSheet] = useState<'session' | 'search' | null>(null);
  const queryClient = useQueryClient();
  const body = useMemo(() => sessionCreateBody(context), [context]);
  const boundCapture = useMemo(
    () =>
      !boundScan
        ? undefined
        : boundScan.kind === 'itemBarcode'
          ? {
              // An item binding verifies the edition, then refreshes the lists and detail behind this screen.
              submit: async (code: { value: string; symbology: string }) => {
                await submitItemBarcodeUpgrade(boundScan.libraryId, boundScan.itemId, code.value);
                await invalidateLibraryItems(queryClient, boundScan.libraryId);
                boundScan.onDone();
              },
            }
          : boundScan.kind === 'cover'
            ? { submit: async () => undefined } // a cover binding waits for the shutter; stray codes are not its row
            : {
                submit: async (code: { value: string; symbology: string }) => {
                  await submitBoundBarcode(boundScan.sessionId, boundScan.candidateId, code);
                  boundScan.onDone();
                },
              },
    [boundScan, queryClient],
  );
  const boundCover = useMemo(
    () => (boundScan?.kind === 'cover' ? { candidateId: boundScan.candidateId, onDone: boundScan.onDone } : undefined),
    [boundScan],
  );
  const session = useScanSession({ libraryId, sources, tuning, sessionBody: body, boundCapture, active });
  const { counters } = useSessionCounters(libraryId, session.sessionId);
  const [flash, setFlash] = useState(false);
  const reduceMotion = useReduceMotion();
  const [counter] = useState(() => createCountingSnapHandler());
  const reportOutcome = useRef<(found: boolean) => void>(() => undefined);
  const pictures = usePipeline({
    deps: pipeline,
    source,
    resendMs: tuning?.resendMs,
    onOutcome: (found) => reportOutcome.current(found),
    boundCover,
    active,
    sessionBody: body,
    noticeMs: tuning?.noticeMs,
  });
  // Scans waiting on this phone for a connection, for the bar's Queued announcement.
  const queued = session.online
    ? 0
    : session.lines.filter((l) => lineStatus(l, false) === 'queued').length + pictures.lines.filter((l) => l.status === 'queued').length;
  const importDeps = useMemo<ImportDeps | undefined>(
    () =>
      pictures.enabled && Platform.OS !== 'web' && pictures.recognition
        ? {
            picker: photoImport?.picker ?? devicePicker,
            vision: photoImport?.vision ?? deviceBarcodes,
            snap: pictures.snapWith,
            recognition: pictures.recognition,
            resize: photoImport?.resize,
            onCodes: (codes) => importCodes.inject(codes),
          }
        : undefined,
    [pictures.enabled, pictures.snapWith, pictures.recognition, photoImport, importCodes],
  );
  const photos = usePhotoImport(importDeps);
  const autoSnap = useAutoSnap({
    source,
    // A bound cover is one deliberate framing: the shutter takes it, auto-snap sits out.
    onSnap: boundCover
      ? (event) => {
          if (event.trigger === 'shutter') pictures.onSnap(event);
        }
      : (onSnap ?? (pictures.enabled ? pictures.onSnap : counter)),
    config: autoSnapConfig,
    tuning: { flashMs: tuning?.flashMs, ...autoSnapTuning },
    active,
  });
  useEffect(() => {
    reportOutcome.current = autoSnap.reportOutcome;
  }, [autoSnap.reportOutcome]);
  useEffect(() => {
    if (!outcomeRef) return;
    outcomeRef.current = autoSnap.reportOutcome;
    return () => {
      outcomeRef.current = null;
    };
  }, [outcomeRef, autoSnap.reportOutcome]);

  const toggleFlash = () => {
    const next = !flash;
    setFlash(next);
    source.setFlash(next);
  };

  return (
    <View style={{ flex: 1, backgroundColor: fixed.black }}>
      {preview}
      <View pointerEvents="box-none" style={[StyleSheet.absoluteFill, { paddingTop: insets.top + spacing.sm }]}>
        <ScannerControls
          libraryId={libraryId}
          context={context}
          showClose={showClose}
          onClose={onClose ?? (() => router.back())}
          onOpenSession={() => setSheet('session')}
          flash={flash}
          onToggleFlash={toggleFlash}
          onOpenSearch={() => setSheet('search')}
          photosEnabled={photos.enabled}
          photosRunning={photos.running}
          onPhotos={photos.start}
        />
        {boundScan ? (
          <View style={boundStyles.wrap}>
            <View style={boundStyles.banner}>
              <Text style={boundStyles.title}>{boundScan.kind === 'cover' ? `Snap the cover for ${boundScan.title}` : `Scan the barcode for ${boundScan.title}`}</Text>
              <Button label="Cancel" variant="onDark" onPress={boundScan.onCancel} />
            </View>
          </View>
        ) : null}

        <View pointerEvents="none" style={{ flex: 1, alignItems: 'center', justifyContent: 'center' }}>
          <NiceScanFlash visible={session.showNiceScan || autoSnap.flash} reduceMotion={reduceMotion} />
        </View>

        {devSource ? (
          <View style={{ alignItems: 'center', gap: spacing.sm, paddingBottom: spacing.sm }}>
            <Button
              label="Scan these 6 codes"
              variant="onDark"
              onPress={() => devSource.inject(DEV_SIX_CODES)}
              style={{ backgroundColor: fixed.scrim }}
            />
            {pictures.enabled ? (
              <Button label="Snap a fixture shelf" variant="onDark" onPress={pictures.replayFixture} style={{ backgroundColor: fixed.scrim }} />
            ) : null}
          </View>
        ) : null}

        <View pointerEvents="box-none" style={{ alignItems: 'center', gap: spacing.md, paddingBottom: spacing.md }}>
          {pictures.enabled && pictures.unreadableCount > 0 ? (
            <Chip label={`Get closer for these ${pictures.unreadableCount}`} onPress={pictures.dismissUnreadable} />
          ) : null}
          {pictures.enabled && pictures.orphanedCount > 0 ? (
            <Chip
              label={
                pictures.orphanedCount === 1
                  ? '1 picture was lost when the app closed'
                  : `${pictures.orphanedCount} pictures were lost when the app closed`
              }
              onPress={pictures.dismissOrphaned}
            />
          ) : null}
          {photos.running && photos.progress ? (
            <View style={{ flexDirection: 'row', alignItems: 'center', gap: spacing.md }}>
              <Text style={{ color: fixed.white }}>{photos.progress}</Text>
              <Chip label="Cancel the rest" onPress={photos.cancel} />
            </View>
          ) : null}
          {!photos.running && photos.message ? <Text style={{ color: fixed.white }}>{photos.message}</Text> : null}
          <TransientHint
            visible={autoSnap.hint}
            label="Try the flash or move closer"
            onPress={() => {
              if (!flash) toggleFlash();
            }}
          />
          <Pressable
            accessibilityRole="button"
            accessibilityLabel="Take picture"
            onPress={autoSnap.takePicture}
            style={{ width: 72, height: 72, borderRadius: 36, borderWidth: 4, borderColor: fixed.white, alignItems: 'center', justifyContent: 'center' }}
          >
            <View style={{ width: 56, height: 56, borderRadius: 28, backgroundColor: fixed.white }} />
          </Pressable>
        </View>

        {sheet === 'session' ? (
          <View style={sheetStyles.wrap}>
            <SessionSheet
              libraryId={libraryId}
              context={context}
              onSave={(_, next) => {
                setContext(next);
                setSheet(null);
              }}
              onClose={() => setSheet(null)}
            />
          </View>
        ) : null}
        {sheet === 'search' ? (
          <View style={sheetStyles.wrap}>
            <ScannerSearch libraryId={libraryId} onClose={() => setSheet(null)} />
          </View>
        ) : null}
        <AutoCommitSnackbar libraryId={libraryId} />
        <View style={{ paddingBottom: insets.bottom, backgroundColor: colors.surface }}>
          <ReceiptBar
            online={session.online}
            items={session.items + pictures.items}
            processing={session.processing + pictures.processing}
            problem={session.problem}
            notice={session.notice ?? pictures.notice}
            counters={counters}
            queued={queued}
            onReview={() => router.push('/review')}
          />
        </View>
      </View>
    </View>
  );
}

const sheetStyles = Unistyles.create((theme) => ({
  wrap: { paddingHorizontal: theme.spacing.lg, paddingBottom: theme.spacing.sm },
}));

const boundStyles = Unistyles.create((theme) => ({
  wrap: { paddingHorizontal: theme.spacing.lg, paddingTop: theme.spacing.sm },
  banner: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: theme.spacing.md,
    backgroundColor: fixed.scrim,
    borderRadius: theme.radius.md,
    padding: theme.spacing.md,
  },
  title: { flex: 1, color: fixed.white },
}));
