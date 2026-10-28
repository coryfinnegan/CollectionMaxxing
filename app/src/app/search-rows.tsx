// The shared-rows fixture route (#457): /search-rows renders the fixture for the
// visual spec in E2E builds and sends production browsers to the library.
import { Redirect } from 'expo-router';
import { SearchRowsFixture } from '@/features/search/fixture';

export default function Route() {
  if (process.env.EXPO_PUBLIC_E2E !== '1') return <Redirect href="/library" />;
  return <SearchRowsFixture />;
}
