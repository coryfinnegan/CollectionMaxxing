// The scanner (docs/06 M3): camera behind everything, "Nice scan!" flash, result cards, the receipt bar.
// It is given its sources, so tests and the dev menu drive it with a FakeScannerSource and no camera.
import { useRouter } from 'expo-router';
import { useContext, useEffect, useMemo, useState, type MutableRefObject, type ReactNode } from 'react';
import { Pressable, StyleSheet, View } from 'react-native';
import { SafeAreaInsetsContext } from 'react-native-safe-area-context';
import { NiceScanFlash } from '../autosnap/NiceScanFlash';
import { DEFAULT_AUTO_SNAP_CONFIG } from '../autosnap/defaults';
import { createCountingSnapHandler, useAutoSnap, type AutoSnapTuning, type SnapEvent } from '../autosnap/useAutoSnap';
import type { AutoSnapConfig } from '../autosnap/types';
import { ResultCards, useReduceMotion } from '../cards/ResultCards';
import { useCardActions } from '../session/useCardActions';
import { useLibraryRole, useScanSession, type SessionTuning } from '../session/useScanSession';
import { ReceiptBar } from '../tray/ReceiptBar';
import { spacing, useColors } from '@/theme/tokens';
import { Button, Chip, Text } from '@/ui';
import { DEV_SIX_CODES } from './devCodes';
import type { FakeScannerSource, ScannerSource } from './ScannerSource';

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
  /** Each picture, from the shutter or auto-snap. The default only counts them; the pipeline is CAM-3b. */
  onSnap?: (event: SnapEvent) => void;
  /** The auto-snap thresholds. CAM-3b passes the server's values. */
  autoSnapConfig?: AutoSnapConfig;
  autoSnapTuning?: AutoSnapTuning;
  /** Filled with a function that tells auto-snap whether the last picture found anything (misfires raise the thresholds). */
  outcomeRef?: MutableRefObject<((found: boolean) => void) | null>;
};

export function ScannerScreen({
  libraryId,
  source,
  preview,
  devSource,
  tuning,
  onClose,
  onSnap,
  autoSnapConfig = DEFAULT_AUTO_SNAP_CONFIG,
  autoSnapTuning,
  outcomeRef,
}: Props) {
  const router = useRouter();
  const colors = useColors();
  const insets = useContext(SafeAreaInsetsContext) ?? { top: 0, bottom: 0, left: 0, right: 0 };
  const sources = useMemo(() => (devSource ? [source, devSource] : [source]), [source, devSource]);
  const session = useScanSession({ libraryId, sources, tuning });
  const { canAdd } = useLibraryRole(libraryId);
  const actions = useCardActions({ libraryId, sessionId: session.sessionId, canAdd });
  const [torch, setTorch] = useState(false);
  const reduceMotion = useReduceMotion();
  const [counter] = useState(() => createCountingSnapHandler());
  const autoSnap = useAutoSnap({
    source,
    onSnap: onSnap ?? counter,
    config: autoSnapConfig,
    tuning: { flashMs: tuning?.flashMs, ...autoSnapTuning },
  });
  useEffect(() => {
    if (!outcomeRef) return;
    outcomeRef.current = autoSnap.reportOutcome;
    return () => {
      outcomeRef.current = null;
    };
  }, [outcomeRef, autoSnap.reportOutcome]);

  const toggleTorch = () => {
    const next = !torch;
    setTorch(next);
    source.setTorch(next);
  };

  return (
    <View style={{ flex: 1, backgroundColor: '#000000' }}>
      {preview}
      <View pointerEvents="box-none" style={[StyleSheet.absoluteFill, { paddingTop: insets.top + spacing.sm }]}>
        <View style={{ flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', paddingHorizontal: spacing.lg }}>
          <Button label="Close" variant="secondary" onPress={onClose ?? (() => router.back())} style={{ backgroundColor: 'rgba(0,0,0,0.5)' }} />
          <Text variant="heading" accessibilityRole="header" style={{ color: '#FFFFFF' }}>
            Scanner
          </Text>
          <Button
            label="Torch"
            variant="secondary"
            accessibilityState={{ selected: torch }}
            onPress={toggleTorch}
            style={{ backgroundColor: torch ? colors.scan : 'rgba(0,0,0,0.5)' }}
          />
        </View>

        <View pointerEvents="none" style={{ flex: 1, alignItems: 'center', justifyContent: 'center' }}>
          <NiceScanFlash visible={session.showNiceScan || autoSnap.flash} reduceMotion={reduceMotion} />
        </View>

        {devSource ? (
          <View style={{ alignItems: 'center', paddingBottom: spacing.sm }}>
            <Button
              label="Scan these 6 codes"
              variant="secondary"
              onPress={() => devSource.inject(DEV_SIX_CODES)}
              style={{ backgroundColor: 'rgba(0,0,0,0.5)' }}
            />
          </View>
        ) : null}

        <View pointerEvents="box-none" style={{ alignItems: 'center', gap: spacing.md, paddingBottom: spacing.md }}>
          {autoSnap.hint ? (
            <Chip
              label="Try the torch or move closer"
              onPress={() => {
                if (!torch) toggleTorch();
              }}
            />
          ) : null}
          <Pressable
            accessibilityRole="button"
            accessibilityLabel="Take picture"
            onPress={autoSnap.takePicture}
            style={{ width: 72, height: 72, borderRadius: 36, borderWidth: 4, borderColor: '#FFFFFF', alignItems: 'center', justifyContent: 'center' }}
          >
            <View style={{ width: 56, height: 56, borderRadius: 28, backgroundColor: '#FFFFFF' }} />
          </Pressable>
        </View>

        <ResultCards cards={session.cards} actions={actions} />
        <View style={{ paddingBottom: insets.bottom, backgroundColor: colors.surface }}>
          <ReceiptBar
            lines={session.lines}
            cards={session.cards}
            online={session.online}
            items={session.items}
            processing={session.processing}
            problem={session.problem}
            notice={session.notice}
            onPlusOne={session.plusOne}
            onRemove={session.remove}
            onReview={() => router.push('/review')}
          />
        </View>
      </View>
    </View>
  );
}
