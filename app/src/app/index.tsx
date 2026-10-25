import { Redirect } from 'expo-router';
import { useEffect, useState } from 'react';
import { registerThisInstall } from '@/features/auth/appSignIn';
import { getNativeAuth, isWeb } from '@/lib/auth/session';

// iPhone: the library if there is a sign-in that works, otherwise Sign in. Web: the library, and a 401 sends the browser to sign-in.
export default function Index() {
  const [target, setTarget] = useState<'/login' | '/library' | null>(isWeb() ? '/library' : null);
  useEffect(() => {
    if (target) return;
    getNativeAuth()
      .hasValidCredentials()
      .then((valid) => {
        // A phone signed in before an update never pressed Sign in on this install: registering here puts it under
        // Devices. Registration never throws and never holds up the library; only the credentials decide the target.
        if (valid) void registerThisInstall();
        setTarget(valid ? '/library' : '/login');
      })
      .catch(() => setTarget('/login'));
  }, [target]);
  return target ? <Redirect href={target} /> : null;
}
