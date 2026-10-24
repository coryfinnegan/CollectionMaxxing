import { View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { BackButton } from '@/features/me/BackButton';
import { MembersScreen } from '@/features/members/MembersScreen';
import { spacing, useColors } from '@/theme/tokens';

// A hidden tab reached from Me: the tab header has no back button, so the route renders its own above the screen.
export default function Route() {
  const colors = useColors();
  return (
    <SafeAreaView edges={['left', 'right']} style={{ flex: 1, backgroundColor: colors.ground }}>
      <View style={{ paddingHorizontal: spacing.lg }}>
        <BackButton />
      </View>
      <MembersScreen />
    </SafeAreaView>
  );
}
