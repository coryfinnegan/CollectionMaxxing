// A seasonal heraldic banner (#362): the BannerMarker's shape with the season's
// cloth beside the season's name. Cosmetic and free; Dragon Off takes it down
// entirely, like every element of this issue.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useDragonAppearance } from '../mascot/appearances';
import { BannerMarker } from './BannerMarker';
import { type CastleMode } from './mode';
import { seasonFor, seasonNames, type Season } from './season';

type SeasonalBannerProps = {
  /** Defaults to the season outside the window; the contact sheet pins each season. */
  season?: Season;
  /** Defaults to the season name ("Winter"). */
  label?: string;
  /** Defaults to the resolved castle mode. */
  mode?: CastleMode;
  testID?: string;
};

export function SeasonalBanner({ season, label, mode, testID }: SeasonalBannerProps) {
  const appearance = useDragonAppearance((s) => s.appearance);
  const resolved = season ?? seasonFor(new Date());
  if (appearance === 'off') return null;
  return <BannerMarker label={label ?? seasonNames[resolved]} season={resolved} mode={mode} testID={testID} />;
}
