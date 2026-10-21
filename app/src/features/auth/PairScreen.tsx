// M1 · Pair with your server (iPhone). A scanned QR code or a typed 6-digit code trades for this device's tokens.
import { useRouter } from 'expo-router';
import { useState } from 'react';
import { PairingError, normalizeServerUrl, pairThisDevice, pairingMessage, parsePairingPayload } from './pairing';
import { FakeQrScanner, type QrScanner, QrScannerUnavailableError } from './qr';
import { getQrScanner } from './services';
import { Button, Card, Screen, Text, TextField } from '@/ui';

export const DEV_SCAN_PAYLOAD = JSON.stringify({ serverUrl: 'https://pc.tailnet.ts.net', token: 'pair-token-ok' });

export function PairScreen() {
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [typing, setTyping] = useState(false);
  const [serverAddress, setServerAddress] = useState('');
  const [code, setCode] = useState('');

  /** `work` resolves true once paired, false if the person backed out. */
  const attempt = async (work: () => Promise<boolean>) => {
    setBusy(true);
    setError(null);
    try {
      if (await work()) router.replace('/library');
      else setBusy(false);
    } catch (e) {
      setError(pairingMessage(e));
      setBusy(false);
    }
  };

  const scan = (scanner: QrScanner) =>
    attempt(async () => {
      let raw: string | null;
      try {
        raw = await scanner.scan();
      } catch (e) {
        if (e instanceof QrScannerUnavailableError) {
          setTyping(true);
          throw new PairingError('scanner');
        }
        throw e;
      }
      if (raw === null) return false;
      const { serverUrl, token } = parsePairingPayload(raw);
      await pairThisDevice(serverUrl, { token });
      return true;
    });

  const typedReady = /^\d{6}$/.test(code.trim()) && serverAddress.trim() !== '';
  const pairWithCode = () =>
    attempt(async () => {
      await pairThisDevice(normalizeServerUrl(serverAddress), { code: code.trim() });
      return true;
    });

  return (
    <Screen title="Pair with your server" subtitle="Pair this iPhone with your library.">
      <Card>
        <Text>1. On your computer, open your server address.</Text>
        <Text>2. Go to Settings, then Devices and browsers, then Add a device.</Text>
        <Text>3. Scan the QR code shown there.</Text>
      </Card>
      {error ? (
        <Text tone="danger" accessibilityRole="alert">
          {error}
        </Text>
      ) : null}
      <Button label="Scan QR code" disabled={busy} onPress={() => scan(getQrScanner())} />
      {typing ? (
        <Card>
          <TextField
            label="Server address"
            value={serverAddress}
            onChangeText={setServerAddress}
            placeholder="pc.tailnet.ts.net"
            autoCapitalize="none"
            autoCorrect={false}
            keyboardType="url"
          />
          <TextField
            label="6-digit code"
            value={code}
            onChangeText={setCode}
            keyboardType="number-pad"
            maxLength={6}
            autoComplete="one-time-code"
          />
          <Button label="Pair this iPhone" busy={busy} disabled={!typedReady} onPress={pairWithCode} />
        </Card>
      ) : (
        <Button label="Enter a 6-digit code" variant="secondary" onPress={() => setTyping(true)} />
      )}
      {__DEV__ ? (
        <Button label="Dev: simulate a scan" variant="secondary" disabled={busy} onPress={() => scan(new FakeQrScanner(DEV_SCAN_PAYLOAD))} />
      ) : null}
    </Screen>
  );
}
