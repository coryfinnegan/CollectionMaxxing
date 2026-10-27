// Settings → Appearance (#426): light or dark, plus the home screen icon where
// the iPhone offers alternates. One design in two schemes; no theme switcher.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useEffect } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { isWeb } from '@/lib/auth/session';
import { applyAppearance, useAppearanceChoice, type AppearanceChoice } from '@/theme/appearance';
import { Card, Chip, LoadingState, Text } from '@/ui';
import { useAppIconSetting } from './appIcon';

export function AppearanceSection() {
  const choice = useAppIconSetting((s) => s.choice);
  const supported = useAppIconSetting((s) => s.supported);
  const ready = useAppIconSetting((s) => s.ready);
  const problem = useAppIconSetting((s) => s.problem);
  const refresh = useAppIconSetting((s) => s.refresh);
  const setIcon = useAppIconSetting((s) => s.set);
  const appearance = useAppearanceChoice((s) => s.choice);
  const setAppearance = useAppearanceChoice((s) => s.set);
  const reloadAppearance = useAppearanceChoice((s) => s.reload);
  useEffect(() => {
    void refresh();
    reloadAppearance();
  }, [refresh, reloadAppearance]);
  useEffect(() => {
    applyAppearance(appearance);
  }, [appearance]);

  return (
    <Card>
      <Text variant="heading">Appearance</Text>
      {!ready && !isWeb() ? <LoadingState label="Checking…" /> : null}
      {ready && supported && !isWeb() ? (
        <>
          <Text>App icon</Text>
          <View style={styles.choices}>
            <Chip label="Default" selected={choice === 'default'} onPress={() => void setIcon('default')} testID="app-icon-default" />
            <Chip label="Game Boy" selected={choice === 'gameboy'} onPress={() => void setIcon('gameboy')} testID="app-icon-gameboy" />
          </View>
          {problem ? (
            <Text tone="danger" accessibilityRole="alert">
              {problem}
            </Text>
          ) : null}
        </>
      ) : null}
      <Text>Light or dark</Text>
      <View style={styles.choices}>
        <ChoiceRow<AppearanceChoice>
          options={[
            { value: 'light', label: 'Light' },
            { value: 'dark', label: 'Dark' },
            { value: 'system', label: 'System' },
          ]}
          name="Appearance"
          current={appearance}
          onPick={setAppearance}
        />
      </View>
    </Card>
  );
}

function ChoiceRow<T extends string>({
  options,
  name,
  current,
  onPick,
}: {
  options: { value: T; label: string }[];
  name: string;
  current: T;
  onPick(value: T): void;
}) {
  return (
    <>
      {options.map((option) => (
        <Chip
          key={option.value}
          label={option.label}
          accessibilityLabel={`${name} ${option.label}`}
          selected={current === option.value}
          onPress={() => onPick(option.value)}
        />
      ))}
    </>
  );
}

const styles = StyleSheet.create((theme) => ({
  choices: { flexDirection: 'row', gap: theme.spacing.sm, flexWrap: 'wrap' },
}));
