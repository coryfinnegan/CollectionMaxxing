import { Redirect } from 'expo-router';
import { useEffect, useState } from 'react';
import { isWeb, restoreSession } from '@/lib/auth/session';

// iPhone: pairing comes first, but only when this device has no stored credentials. Web: the library, and a 401 sends
// the browser to sign-in.
export default function Index() {
  const [target, setTarget] = useState<'/pair' | '/library' | null>(isWeb() ? '/library' : null);
  useEffect(() => {
    if (target) return;
    restoreSession().then((paired) => setTarget(paired ? '/library' : '/pair'));
  }, [target]);
  return target ? <Redirect href={target} /> : null;
}
