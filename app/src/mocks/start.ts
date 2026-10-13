// Mock mode on iOS: msw/native intercepts fetch inside the JS runtime.
import 'react-native-url-polyfill/auto';
import { setupServer } from 'msw/native';
import { handlers } from './handlers';

export async function startMocking(): Promise<void> {
  setupServer(...handlers).listen({ onUnhandledRequest: 'bypass' });
}
