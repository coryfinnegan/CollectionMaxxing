// A themed state (#363): the mascot's home. A parchment notice carrying a state
// pose above plain-language words (the caller's own EmptyState or LoadingState
// content). The pose honors the Dragon appearances setting; the words stay first.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import type { ReactNode } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { PlacedMascot } from '../mascot/PlacedMascot';
import type { MascotPose } from '../mascot/poses';
import { type CastleMode, useCastleMode } from './mode';
import { NineSlice } from './NineSlice';

type CastleStateProps = {
  pose: MascotPose;
  children: ReactNode;
  /** Defaults to the resolved castle mode. */
  mode?: CastleMode;
  testID?: string;
};

export function CastleState({ pose, children, mode, testID }: CastleStateProps) {
  const hooked = useCastleMode();
  return (
    <NineSlice surface="parchment" mode={mode ?? hooked} testID={testID}>
      <View style={styles.state}>
        <PlacedMascot pose={pose} placement="state" size={120} testID="castle-state-dragon" />
        {children}
      </View>
    </NineSlice>
  );
}

const styles = StyleSheet.create((theme) => ({
  state: { alignItems: 'center', gap: theme.spacing.md },
}));
