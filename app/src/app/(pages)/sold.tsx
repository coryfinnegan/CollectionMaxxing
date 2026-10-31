import { Platform } from 'react-native';
import { LibraryView } from '@/features/library/LibraryView';

// What you sold, traded or gave away: the library's own view with a fixed status, kept out of the library and its counts.
// A pushed page with a stack header on the phone, so the content title hides there; the web has no header, so it stays.
export default function Route() {
  return <LibraryView title="Sold" fixedStatus="sold" hideTitle={Platform.OS !== 'web'} />;
}
