// An account with no libraries belongs on Welcome, not on an empty library page. Web only; the phone's version is ONB-3.
import { useRouter } from 'expo-router';
import { useEffect } from 'react';
import { isWeb } from '@/lib/auth/session';
import { useLibraries } from './useLibraries';

export function LibraryGate() {
  const router = useRouter();
  const { libraries, loaded } = useLibraries();
  const empty = loaded && libraries.length === 0;
  useEffect(() => {
    if (isWeb() && empty) router.replace('/welcome');
  }, [empty, router]);
  return null;
}
