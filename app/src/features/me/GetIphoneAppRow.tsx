// Settings > Get the iPhone app (web only): the .ipa the server synced from the latest release, with its date, size and checksum.
import { useState } from 'react';
import { useGetIpaInfo } from '@/api/generated/app/app';
import { friendlyError } from '@/lib/problem';
import { Button, Text } from '@/ui';
import { getSharer } from './services';

const IPA_URL = '/api/v1/app/ipa';

function formatSize(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${Math.max(1, Math.round(bytes / 1024))} KB`;
}

function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: 'numeric' });
}

export function GetIphoneAppRow() {
  const info = useGetIpaInfo();
  const [message, setMessage] = useState<string | null>(null);
  if (info.isPending) return <Text tone="muted">Checking…</Text>;

  const ipa = info.data?.status === 200 ? info.data.data : null;
  const download = async () => {
    setMessage(null);
    try {
      await getSharer().deliver(IPA_URL);
    } catch (e) {
      setMessage(friendlyError(e));
    }
  };

  return (
    <>
      <Text variant="heading">Get the iPhone app</Text>
      {ipa ? (
        <>
          <Text>{`Latest build: ${formatDate(ipa.modifiedAt)}, ${formatSize(ipa.sizeBytes)}.`}</Text>
          {ipa.sha256 ? <Text tone="muted">{`SHA-256: ${ipa.sha256}`}</Text> : null}
          {message ? <Text tone="danger" accessibilityRole="alert">{message}</Text> : null}
          <Button label="Download the iPhone app" onPress={() => void download()} />
          <Text tone="muted">Install it with AltStore on your iPhone: open AltStore, then My Apps, then the plus button, and choose this file.</Text>
        </>
      ) : (
        <Text tone="muted">Not available yet: it appears after the next release.</Text>
      )}
    </>
  );
}
