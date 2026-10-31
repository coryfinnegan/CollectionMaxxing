// The Search view fixture route (#458): /search?state=... renders one Search
// view state for the visual spec in E2E builds and sends production browsers
// to the library.
// The flag is inlined at build time and the fixture module is required inside the
// branch (never imported at the top), so a production export drops the fixture.
import { Redirect, useLocalSearchParams } from 'expo-router';
import type { SearchViewFixtureState } from '@/features/search/fixture';

const STATES: SearchViewFixtureState[] = ['initial', 'loading', 'results', 'none', 'error', 'offline'];

export default function Route() {
  const { state } = useLocalSearchParams<{ state?: string }>();
  if (process.env.EXPO_PUBLIC_E2E === '1') {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { SearchViewFixture } = require('@/features/search/fixture') as typeof import('@/features/search/fixture');
    return <SearchViewFixture state={STATES.find((s) => s === state) ?? 'initial'} />;
  }
  return <Redirect href="/library" />;
}
