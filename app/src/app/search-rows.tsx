// The shared-rows fixture route (#457): /search-rows renders the fixture for the
// visual spec in E2E builds and sends production browsers to the library.
// The flag is inlined at build time and the fixture module is required inside the
// branch (never imported at the top), so a production export drops the fixture.
import { Redirect } from 'expo-router';

export default function Route() {
  if (process.env.EXPO_PUBLIC_E2E === '1') {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { SearchRowsFixture } = require('@/features/search/fixture') as typeof import('@/features/search/fixture');
    return <SearchRowsFixture />;
  }
  return <Redirect href="/library" />;
}
