import { SignInScreen } from '@/features/auth/NativeAuthScreens';
import { LoginScreen } from '@/features/auth/WebAuthScreens';
import { isWeb } from '@/lib/auth/session';

export default function Login() {
  return isWeb() ? <LoginScreen /> : <SignInScreen />;
}
