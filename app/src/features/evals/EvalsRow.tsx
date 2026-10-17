// The "Run evals" row in Settings → Recognition. It shows only on the iPhone, and only when the PC lists golden photos for this person:
// that is the owner with evals turned on. A family member (403), a PC with evals off (404) or the web gets nothing.
import { useQuery } from '@tanstack/react-query';
import { useRouter } from 'expo-router';
import { isWeb } from '@/lib/auth/session';
import { Button, Text } from '@/ui';
import { evalApi } from './api';

export function useEvalsAvailable(): boolean {
  const web = isWeb();
  const probe = useQuery({
    queryKey: ['evals', 'golden'],
    queryFn: () => evalApi.list(),
    enabled: !web,
    retry: false,
    staleTime: 60_000,
  });
  return !web && probe.isSuccess;
}

export function EvalsRow() {
  const router = useRouter();
  const available = useEvalsAvailable();
  if (!available) return null;
  return (
    <>
      <Text tone="muted">Reads your PC&apos;s test photos on this iPhone to check how well scanning works.</Text>
      <Button label="Run evals" variant="secondary" onPress={() => router.push('/evals')} />
    </>
  );
}
