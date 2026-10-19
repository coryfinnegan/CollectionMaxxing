// Which build is running: release.yml and ios-build.yml set these when they bundle the app (Expo inlines EXPO_PUBLIC_*
// values at build time). A local or test build has neither, and About shows no build line.
export function buildLine(
  sha: string | undefined = process.env.EXPO_PUBLIC_BUILD_SHA,
  builtAt: string | undefined = process.env.EXPO_PUBLIC_BUILD_TIME,
): string | null {
  if (!sha) return null;
  const line = `Build ${sha.slice(0, 7)}`;
  const date = builtAt ? new Date(builtAt) : null;
  if (!date || Number.isNaN(date.getTime())) return line;
  return `${line} from ${date.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })}`;
}
