import { type Page, expect, test as base } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

// Every test gets a fresh browser context and a fresh page load, so MSW's in-memory mock state starts from the seed.
// Specs therefore move around by clicking (a full page load would reset the mock data) unless they want a reset.

/** Serious or critical axe findings that are accepted for now. Empty: every finding fails the scan. */
const knownIssues: { id: string; target?: RegExp }[] = [];

type Fixtures = {
  /** Console errors and uncaught page errors seen so far. */
  errors: string[];
  /** Opens a route and waits for the mocked app to be ready. */
  open: (path: string) => Promise<void>;
  /** Fails if the page has serious or critical accessibility violations. */
  expectAccessible: () => Promise<void>;
  /** Writes a PR screenshot when SCREENSHOTS=<dir> is set (otherwise does nothing). */
  shot: (name: string) => Promise<void>;
};

export const test = base.extend<Fixtures>({
  errors: async ({ page }, use) => {
    const errors: string[] = [];
    page.on('console', (m) => {
      if (m.type() === 'error') errors.push(m.text());
    });
    page.on('pageerror', (e) => errors.push(e.message));
    await use(errors);
  },

  open: async ({ page, errors }, use) => {
    void errors;
    await use(async (path) => {
      await page.goto(path);
      await page.waitForFunction(() => '__cm' in window, undefined, { timeout: 20_000 });
    });
  },

  expectAccessible: async ({ page }, use) => {
    await use(async () => {
      // Entrance animations (at most a 160ms stagger plus 150ms) settle before the scan: axe reads
      // opacity into contrast, so a mid-fade chip would fail a check its settled state passes (#304).
      await page.waitForTimeout(400);
      const result = await new AxeBuilder({ page }).analyze();
      const found = result.violations
        .filter((v) => v.impact === 'serious' || v.impact === 'critical')
        .flatMap((v) =>
          v.nodes
            .map((n) => n.target.join(' '))
            .filter((target) => !knownIssues.some((k) => k.id === v.id && (!k.target || k.target.test(target))))
            .map((target) => `${v.id} (${v.impact}): ${target}`),
        );
      expect(found).toEqual([]);
    });
  },

  shot: async ({ page }, use) => {
    await use(async (name) => {
      const dir = process.env.SCREENSHOTS;
      if (!dir) return;
      mkdirSync(dir, { recursive: true });
      await page.screenshot({ path: join(dir, `${name}.png`) });
    });
  },
});

export { expect };

/** True when the page has no horizontal scroll (the phone-width check). */
export async function fitsWidth(page: Page): Promise<boolean> {
  return page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth && document.body.scrollWidth <= window.innerWidth);
}

