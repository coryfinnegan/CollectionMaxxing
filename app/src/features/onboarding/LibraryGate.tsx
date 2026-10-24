// An account with no libraries belongs on Welcome, not on an empty library page. Web and phone.
import { useRouter } from 'expo-router';
import { useEffect } from 'react';
import { useLibraries } from './useLibraries';

export function LibraryGate() {
  const router = useRouter();
  const { libraries, loaded } = useLibraries();
  const empty = loaded && libraries.length === 0;
  useEffect(() => {
    if (empty) router.replace('/welcome');
  }, [empty, router]);
  return null;
}
