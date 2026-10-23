// Mock mode on the web: the MSW service worker in the browser (public/mockServiceWorker.js).
import { setupWorker } from 'msw/browser';
import { handlers } from './handlers';

export async function startMocking(): Promise<void> {
  const worker = setupWorker(...handlers);
  await worker.start({ onUnhandledRequest: 'bypass', quiet: true });
  // Test-only hooks for the Playwright specs (docs/09). The flag is inlined at build time, so a production export drops this branch.
  if (process.env.EXPO_PUBLIC_E2E === '1') await installTestHooks(worker);
}

async function installTestHooks(worker: ReturnType<typeof setupWorker>): Promise<void> {
  const { http, HttpResponse } = await import('msw');
  const { itemsState } = await import('./items');
  const { mockAccount } = await import('./data');
  // `?account=new` opens as a brand-new account with no libraries; `?account=signedout` as a browser with no session.
  const account = new URLSearchParams(window.location.search).get('account');
  if (account === 'new') mockAccount.libraries = [];
  if (account === 'signedout') mockAccount.signedIn = false;
  (window as unknown as { __cm: unknown }).__cm = {
    /** Makes every API call answer 401, like a session that was revoked or timed out. */
    expireSession: () =>
      worker.use(
        http.all('*/api/v1/*', () =>
          HttpResponse.json({ title: 'Unauthorized', status: 401 }, { status: 401, headers: { 'Content-Type': 'application/problem+json' } }),
        ),
      ),
    /** Another member changed the item: bumps its version so the loaded ETag goes stale. */
    changeItemElsewhere: (id: string) => {
      const entry = itemsState.entries.find((e) => e.item.id === id);
      if (entry) entry.version += 1;
    },
  };
}
