// The scanner (docs/06 M3): camera behind everything, "Nice scan!" flash, result cards, the receipt bar.
// It is given its sources, so tests and the dev menu drive it with a FakeScannerSource and no camera.
import { useRouter } from 'expo-router';
import { useContext, useMemo, useState, type ReactNode } from 'react';
import { StyleSheet, View } from 'react-native';
import { SafeAreaInsetsContext } from 'react-native-safe-area-context';
import { ResultCards } from '../cards/ResultCards';
import { useCardActions } from '../session/useCardActions';
import { useLibraryRole, useScanSession, type SessionTuning } from '../session/useScanSession';
import { ReceiptBar } from '../tray/ReceiptBar';
import { spacing, useColors } from '@/theme/tokens';
import { Button, Text } from '@/ui';
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
};

export function ScannerScreen({ libraryId, source, preview, devSource, tuning, onClose }: Props) {
  const router = useRouter();
  const colors = useColors();
  const insets = useContext(SafeAreaInsetsContext) ?? { top: 0, bottom: 0, left: 0, right: 0 };
  const sources = useMemo(() => (devSource ? [source, devSource] : [source]), [source, devSource]);
  const session = useScanSession({ libraryId, sources, tuning });
  const { canAdd } = useLibraryRole(libraryId);
  const actions = useCardActions({ libraryId, sessionId: session.sessionId, canAdd });
  const [torch, setTorch] = useState(false);

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
          {session.showNiceScan ? (
            <View
              accessibilityLiveRegion="polite"
              style={{ backgroundColor: colors.scan, paddingHorizontal: spacing.xl, paddingVertical: spacing.md, borderRadius: 999 }}
            >
              <Text variant="heading" style={{ color: colors.onScan }}>
                Nice scan!
              </Text>
            </View>
          ) : null}
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
