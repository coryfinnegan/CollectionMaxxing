// Settings → Appearance: the library design, the castle theme, the dragon, and
// light or dark, plus the home screen icon where the iPhone offers alternates.
// Native controls throughout: Settings itself takes no castle theme.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useEffect } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { useLibraryDesign, type LibraryDesign } from '@/features/library/design';
import { isWeb } from '@/lib/auth/session';
import { applyAppearance, useAppearanceChoice, type AppearanceChoice } from '@/theme/appearance';
import { Card, Chip, LoadingState, Text } from '@/ui';
import { useCastleThemeSetting, type CastleThemeSetting } from '@/ui/castle/mode';
import { useDragonAppearance, type DragonAppearance } from '@/ui/mascot/appearances';
import { useAppIconSetting } from './appIcon';

export function AppearanceSection() {
  const choice = useAppIconSetting((s) => s.choice);
  const supported = useAppIconSetting((s) => s.supported);
  const ready = useAppIconSetting((s) => s.ready);
  const problem = useAppIconSetting((s) => s.problem);
  const refresh = useAppIconSetting((s) => s.refresh);
  const setIcon = useAppIconSetting((s) => s.set);
  const theme = useCastleThemeSetting((s) => s.setting);
  const setTheme = useCastleThemeSetting((s) => s.set);
  const reloadTheme = useCastleThemeSetting((s) => s.reload);
  const dragon = useDragonAppearance((s) => s.appearance);
  const setDragon = useDragonAppearance((s) => s.set);
  const reloadDragon = useDragonAppearance((s) => s.reload);
  const appearance = useAppearanceChoice((s) => s.choice);
  const setAppearance = useAppearanceChoice((s) => s.set);
  const reloadAppearance = useAppearanceChoice((s) => s.reload);
  const design = useLibraryDesign((s) => s.choice);
  const setDesign = useLibraryDesign((s) => s.set);
  const reloadDesign = useLibraryDesign((s) => s.reload);
  useEffect(() => {
    void refresh();
    reloadTheme();
    reloadDragon();
    reloadAppearance();
    reloadDesign();
  }, [refresh, reloadTheme, reloadDragon, reloadAppearance, reloadDesign]);
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
      <Text>Castle theme</Text>
      <View style={styles.choices}>
        <ChoiceRow<CastleThemeSetting>
          options={[
            { value: 'full', label: 'Full' },
            { value: 'quiet', label: 'Quiet' },
          ]}
          name="Theme"
          current={theme}
          onPick={setTheme}
        />
      </View>
      <Text tone="muted">Full frames the library in the castle. Quiet keeps the shape without the textures.</Text>
      <Text>Dragon appearances</Text>
      <View style={styles.choices}>
        <ChoiceRow<DragonAppearance>
          options={[
            { value: 'full', label: 'Full' },
            { value: 'minimal', label: 'Minimal' },
            { value: 'off', label: 'Off' },
          ]}
          name="Dragon"
          current={dragon}
          onPick={setDragon}
        />
      </View>
      <Text tone="muted">Minimal keeps the dragon to empty, loading and error states. Off hides it everywhere.</Text>
      <Text>Library design</Text>
      <View style={styles.choices}>
        <ChoiceRow<LibraryDesign>
          options={[
            { value: 'current', label: 'Current' },
            { value: 'castle', label: 'Castle' },
            { value: 'mall', label: 'Mall' },
          ]}
          name="Library design"
          current={design}
          onPick={setDesign}
        />
      </View>
      <Text tone="muted">Castle shelves your collection in stone. Mall walks it as 90s stores. Current stays the fast grid.</Text>
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
