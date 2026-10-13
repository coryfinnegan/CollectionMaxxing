// Mock mode on the web: the MSW service worker in the browser (public/mockServiceWorker.js).
import { setupWorker } from 'msw/browser';
import { handlers } from './handlers';

export async function startMocking(): Promise<void> {
  await setupWorker(...handlers).start({ onUnhandledRequest: 'bypass', quiet: true });
}
