// The swappable pieces of the auth screens, set once at app start (and replaced by fakes in tests).
import { BrowserPasskeyClient, FakePasskeyClient, type PasskeyClient } from './passkeys';
import { type QrRenderer, type QrScanner, UnavailableQrScanner } from './qr';
import { QrPlaceholder } from './QrPlaceholder';

// Mock mode (no real server to register a passkey with) uses the scripted client.
let passkeys: PasskeyClient = process.env.EXPO_PUBLIC_API_MOCK === '1' ? new FakePasskeyClient() : new BrowserPasskeyClient();
let scanner: QrScanner = new UnavailableQrScanner();
let qrRenderer: QrRenderer = QrPlaceholder;

export const getPasskeys = () => passkeys;
export const setPasskeys = (client: PasskeyClient) => {
  passkeys = client;
};
export const getQrScanner = () => scanner;
export const setQrScanner = (next: QrScanner) => {
  scanner = next;
};
export const getQrRenderer = () => qrRenderer;
export const setQrRenderer = (next: QrRenderer) => {
  qrRenderer = next;
};
