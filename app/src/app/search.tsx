// The Search view fixture route (#458): /search?state=... renders one Search
// view state for the visual spec in E2E builds and sends production browsers
// to the library.
import { Redirect, useLocalSearchParams } from 'expo-router';
import { SearchViewFixture, type SearchViewFixtureState } from '@/features/search/fixture';

const STATES: SearchViewFixtureState[] = ['initial', 'loading', 'results', 'none', 'error', 'offline'];

export default function Route() {
  const { state } = useLocalSearchParams<{ state?: string }>();
  if (process.env.EXPO_PUBLIC_E2E !== '1') return <Redirect href="/library" />;
  return <SearchViewFixture state={STATES.find((s) => s === state) ?? 'initial'} />;
}
