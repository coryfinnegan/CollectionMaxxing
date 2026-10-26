// The seasonal banners (#362): one heraldic cloth per season, rotating with the
// calendar. Meteorological seasons for the northern hemisphere (December is
// winter); the southern half of the world gets the same banners on the same
// dates, which the copy never claims otherwise. Every cloth reuses a castle
// graphics token, so no new colors enter the palette.
import { castle } from '@/theme/tokens';

export type Season = 'spring' | 'summer' | 'autumn' | 'winter';

export const seasonNames: Record<Season, string> = { spring: 'Spring', summer: 'Summer', autumn: 'Autumn', winter: 'Winter' };

/** The season a date falls in: spring from March, summer from June, autumn from September, winter from December. */
export function seasonFor(date: Date): Season {
  const month = date.getMonth();
  if (month >= 2 && month <= 4) return 'spring';
  if (month >= 5 && month <= 7) return 'summer';
  if (month >= 8 && month <= 10) return 'autumn';
  return 'winter';
}

/** The season outside the window right now. */
export function useSeason(): Season {
  return seasonFor(new Date());
}

/** The castle cloth each season hangs: green shoots, brass sun, russet leaves, cold blue. */
export const seasonCloth: Record<Season, keyof typeof castle> = {
  spring: 'bannerGame',
  summer: 'brass',
  autumn: 'bannerBook',
  winter: 'bannerFilm',
};
