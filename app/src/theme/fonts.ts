// The app's faces, loaded before the first screen: Bricolage Grotesque for display and
// UI, Space Mono for catalog metadata. The map keys are the families the type scale names.
import {
  BricolageGrotesque_400Regular,
  BricolageGrotesque_600SemiBold,
  BricolageGrotesque_700Bold,
  BricolageGrotesque_800ExtraBold,
} from '@expo-google-fonts/bricolage-grotesque';
import { SpaceMono_400Regular } from '@expo-google-fonts/space-mono';
import { useFonts } from 'expo-font';
import { typeScale } from './tokens';

export const fontAssets = {
  [typeScale.body.fontFamily]: BricolageGrotesque_400Regular,
  [typeScale.label.fontFamily]: BricolageGrotesque_600SemiBold,
  [typeScale.heading.fontFamily]: BricolageGrotesque_700Bold,
  [typeScale.display.fontFamily]: BricolageGrotesque_800ExtraBold,
  [typeScale.mono.fontFamily]: SpaceMono_400Regular,
};

export function useAppFonts(): boolean {
  const [loaded] = useFonts(fontAssets);
  return loaded;
}
