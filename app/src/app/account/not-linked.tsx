import { NativeNotLinkedScreen } from '@/features/auth/NativeAuthScreens';
import { NotLinkedScreen } from '@/features/auth/WebAuthScreens';
import { isWeb } from '@/lib/auth/session';

export default function NotLinked() {
  return isWeb() ? <NotLinkedScreen /> : <NativeNotLinkedScreen />;
}
