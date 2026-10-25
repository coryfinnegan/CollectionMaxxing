import { Platform } from 'react-native';
import { LibraryView } from '@/features/library/LibraryView';

// A pushed page with a stack header on the phone, so the content title hides there; the web has no header, so it stays.
export default function Route() {
  return <LibraryView title="Wishlist" fixedStatus="wishlist" hideTitle={Platform.OS !== 'web'} />;
}
