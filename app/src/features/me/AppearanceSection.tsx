// Settings → Appearance (iPhone only): the home screen icon, default dragon or Game Boy alternate (#364).
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useEffect } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { Card, Chip, LoadingState, Text } from '@/ui';
import { useAppIconSetting } from './appIcon';

export function AppearanceSection() {
  const choice = useAppIconSetting((s) => s.choice);
  const supported = useAppIconSetting((s) => s.supported);
  const ready = useAppIconSetting((s) => s.ready);
  const problem = useAppIconSetting((s) => s.problem);
  const refresh = useAppIconSetting((s) => s.refresh);
  const set = useAppIconSetting((s) => s.set);
  useEffect(() => {
    void refresh();
  }, [refresh]);

  if (ready && !supported) return null;
  return (
    <Card>
      <Text variant="heading">Appearance</Text>
      {ready ? (
        <>
          <Text>App icon</Text>
          <View style={styles.choices}>
            <Chip label="Default" selected={choice === 'default'} onPress={() => void set('default')} testID="app-icon-default" />
            <Chip label="Game Boy" selected={choice === 'gameboy'} onPress={() => void set('gameboy')} testID="app-icon-gameboy" />
          </View>
          {problem ? (
            <Text tone="danger" accessibilityRole="alert">
              {problem}
            </Text>
          ) : null}
        </>
      ) : (
        <LoadingState label="Checking…" />
      )}
    </Card>
  );
}

const styles = StyleSheet.create((theme) => ({
  choices: { flexDirection: 'row', gap: theme.spacing.sm, flexWrap: 'wrap' },
}));
