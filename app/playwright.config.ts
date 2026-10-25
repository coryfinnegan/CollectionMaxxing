import { defineConfig, devices } from '@playwright/test';

// Specs run against `expo export --platform web` with MSW on (no real API), served from a random local port.
// Never 8080, 8409, 32400, 8096 or 5480 (docs: CLAUDE.md "Ports in use"). Workers re-read this file, so the port is fixed in the env once.
const forbidden = new Set([8080, 8409, 32400, 8096, 5480]);
function pickPort(): number {
  for (;;) {
    const port = 41000 + Math.floor(Math.random() * 9000);
    if (!forbidden.has(port)) return port;
  }
}
const port = Number(process.env.E2E_PORT ?? pickPort());
process.env.E2E_PORT = String(port);

export default defineConfig({
  testDir: './playwright',
  testMatch: '**/*.spec.ts',
  fullyParallel: true,
  workers: process.env.CI ? 2 : 4,
  retries: 0,
  timeout: 30_000,
  expect: { timeout: 7_000 },
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : [['list']],
  outputDir: './test-results',
  // One snapshot per screen for every OS (#307): CI runs Linux, dev runs
  // Windows, and font rasterization differs. Hinting is the biggest OS delta,
  // so it is off everywhere (functional tests are unaffected); the spec's
  // maxDiffPixelRatio absorbs the remaining anti-aliasing while layout, color,
  // or title regressions still fail.
  snapshotPathTemplate: '{testDir}/{testFilePath}-snapshots/{arg}-{projectName}{ext}',
  use: {
    baseURL: `http://127.0.0.1:${port}`,
    viewport: { width: 1280, height: 800 },
    serviceWorkers: 'allow',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    launchOptions: { args: ['--font-render-hinting=none'] },
  },
  projects: [
    { name: 'chromium', testIgnore: '**/mobile.spec.ts', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } } },
    {
      // A phone-sized Chromium (CI installs only chromium, so no WebKit): 390x844, touch, mobile viewport handling.
      name: 'mobile',
      testMatch: '**/mobile.spec.ts',
      use: { ...devices['Desktop Chrome'], viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true },
    },
  ],
  webServer: {
    command: `node playwright/serve.mjs ${port}`,
    url: `http://127.0.0.1:${port}/login`,
    timeout: 240_000,
    reuseExistingServer: false,
    stdout: 'ignore',
    stderr: 'pipe',
  },
});
