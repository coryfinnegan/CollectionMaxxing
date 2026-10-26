// A placed dragon (#363): the pose library behind the Dragon appearances setting.
// Decorative placements (the Library header perch) show on Full only; state
// placements (empty, loading, error, milestone, celebration) also show on Minimal.
// Off shows nothing. Motion still follows the Mascot's own rules (Reduce Motion
// holds every pose still; Minimal holds them still too).
import { placementVisible, useDragonAppearance, type MascotPlacement } from './appearances';
import { Mascot } from './Mascot';
import type { MascotPose } from './poses';

type PlacedMascotProps = {
  pose: MascotPose;
  placement: MascotPlacement;
  size?: number;
  /** When the pose conveys state with no adjacent text carrying it, the label screen readers announce. */
  accessibilityLabel?: string;
  testID?: string;
};

export function PlacedMascot({ pose, placement, size, accessibilityLabel, testID = 'mascot' }: PlacedMascotProps) {
  const appearance = useDragonAppearance((s) => s.appearance);
  if (!placementVisible(appearance, placement)) return null;
  return <Mascot pose={pose} size={size} accessibilityLabel={accessibilityLabel} testID={testID} />;
}
