// Visual regression for the key screens (#307): committed snapshots, compared by
// Playwright on every e2e run at zero cost. Regenerate deliberately with
// `npx playwright test visual --update-snapshots` and review the diff.
import { type Page } from '@playwright/test';
import { expect, test } from './fixtures';

const matrix = 'c0000000-0000-4000-8000-000000000001';

async function expectShot(page: Page, name: string) {
  // Fonts gate the first screen, but rasterization settles on its own clock.
  await page.evaluate(() => document.fonts.ready);
  // Scrollbar styling differs per OS; it is chrome, not the design under test.
  await page.addStyleTag({ content: '* { scrollbar-width: none !important; } *::-webkit-scrollbar { display: none !important; }' });
  // 0.015 absorbs measured Linux-vs-Windows rasterization (PR #329 CI: 0.01 on
  // library-table/members, 0.03 on about before unhinted rendering) while a
  // real regression still fails: a doubled title moved about by 0.03.
  await expect(page).toHaveScreenshot(`${name}.png`, { animations: 'disabled', maxDiffPixelRatio: 0.015 });
}

test.describe('visual', () => {
  test('library table', async ({ page, open }) => {
    await open('/library');
    await expect(page.getByRole('grid', { name: 'Library table' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Open The Matrix' })).toBeVisible();
    await expectShot(page, 'library-table');
  });

  test('library grid', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('button', { name: 'Grid view' }).click();
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toBeVisible();
    await expectShot(page, 'library-grid');
  });

  test('item detail', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('link', { name: 'Open The Matrix' }).click();
    await expect(page).toHaveURL(new RegExp(`/item/${matrix}$`));
    await expect(page.getByRole('heading', { name: 'The Matrix' })).toBeVisible();
    await expectShot(page, 'item-detail');
  });

  // Logged-out routes show no authenticated nav (#328): no sidebar beside the sign-in form.
  test('sign-in', async ({ page, open }) => {
    await open('/login');
    await expect(page.getByText('Sign in to your library.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Sign in', exact: true })).toBeVisible();
    await expectShot(page, 'sign-in');
  });

  test('members', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('link', { name: 'Members' }).click();
    await expect(page).toHaveURL(/\/members$/);
    await expect(page.getByText('Cory (you)')).toBeVisible();
    await expectShot(page, 'members');
  });

  test('about', async ({ page, open }) => {
    await open('/settings');
    await page.getByRole('button', { name: 'About and privacy' }).click();
    await expect(page).toHaveURL(/\/about$/);
    await expect(page.getByText('Provider data')).toBeVisible();
    await expectShot(page, 'about');
  });

  test('me', async ({ page, open }) => {
    await open('/me');
    await expect(page.getByText('This browser')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Locations' })).toBeVisible();
    // The counts settle after the stats and members calls; the shot waits for them.
    await expect(page.getByText(/\d+ items · \d+ members/)).toBeVisible();
    await expectShot(page, 'me');
  });

  test('settings appearance', async ({ page, open }) => {
    await open('/settings');
    await expect(page.getByRole('button', { name: 'Appearance Light' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Appearance Dark' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Appearance System' })).toBeVisible();
    await expectShot(page, 'settings-appearance');
  });

  // The shared toast (#465): the delete Undo and the review commit Undo, each in
  // both schemes, so an unthemed bar or an invisible action label fails here.
  test.describe('toasts', () => {
    test('delete toast', async ({ page, open }) => {
      await openDeleteToast(page, open);
      await expectShot(page, 'delete-toast');
    });

    test('review commit toast', async ({ page, open }) => {
      await openCommitToast(page, open);
      await expectShot(page, 'review-toast');
    });

    test.describe('dark', () => {
      test.use({ colorScheme: 'dark' });

      test('delete toast dark', async ({ page, open }) => {
        await openDeleteToast(page, open);
        await expectShot(page, 'delete-toast-dark');
      });

      test('review commit toast dark', async ({ page, open }) => {
        await openCommitToast(page, open);
        await expectShot(page, 'review-toast-dark');
      });
    });
  });

  // The revised scanner scope (#432): captures queue silently, and every capture's state is a Review row
  // with its thumbnail. Matched rows aggregate, so the spec expands them; the rows do not fit one viewport,
  // so each scheme shoots the top (needing attention) and the tail (matched + still looking up).
  test.describe('review rows seeded', () => {
    test.use({ viewport: { width: 1280, height: 1500 } });

    test('review rows', async ({ page, open }) => {
      await openSeededReview(page, open);
      await page.getByText('Pick one · 1').scrollIntoViewIfNeeded();
      await expectShot(page, 'review-rows');
      await page.getByText('You own these · 1').evaluate((el) => el.scrollIntoView({ block: 'start', behavior: 'instant' as ScrollBehavior }));
      await expectShot(page, 'review-rows-tail');
      // One tap moves the pick-one selection; Add N still counts the row.
      await page.getByRole('radio', { name: 'Select Suddenly You (2001), Other' }).click();
      await expect(page.getByRole('radio', { name: 'Select Suddenly You (2001), Other' })).toBeChecked();
      await expect(page.getByRole('button', { name: 'Add 2 items' })).toBeVisible();
      // Tapping a row opens its Result screen.
      await page.getByRole('button', { name: 'Open result for Super Mario 64' }).click();
      await expect(page).toHaveURL(/\/result\/e2e00000-0000-4000-8000-000000000014$/);
      await expect(page.getByRole('heading', { name: 'Super Mario 64' })).toBeVisible();
    });

    test.describe('dark', () => {
      test.use({ colorScheme: 'dark' });

      test('review rows dark', async ({ page, open }) => {
        await openSeededReview(page, open);
        await page.getByText('Pick one · 1').scrollIntoViewIfNeeded();
        await expectShot(page, 'review-rows-dark');
        await page.getByText('You own these · 1').evaluate((el) => el.scrollIntoView({ block: 'start', behavior: 'instant' as ScrollBehavior }));
        await expectShot(page, 'review-rows-tail-dark');
      });
    });
  });

  // The shared work and edition rows (#457): an owned multi-edition work, an
  // unowned single, an unowned multi with no art, then the owned work's editions.
  test.describe('search rows seeded', () => {
    test.use({ viewport: { width: 1280, height: 1500 } });

    test('search rows', async ({ page, open }) => {
      await openSearchRows(page, open);
      await expectShot(page, 'search-rows');
    });

    test.describe('dark', () => {
      test.use({ colorScheme: 'dark' });

      test('search rows dark', async ({ page, open }) => {
        await openSearchRows(page, open);
        await expectShot(page, 'search-rows-dark');
      });
    });
  });
});

async function openDeleteToast(page: Page, open: (path: string) => Promise<void>) {
  await open('/library');
  await page.getByRole('button', { name: 'Grid view' }).click();
  await page.getByRole('button', { name: 'The Matrix, DVD' }).click();
  await page.getByRole('button', { name: 'Delete', exact: true }).click();
  await expect(page.getByText('Deleted The Matrix')).toBeVisible();
  await page.getByRole('button', { name: 'Undo' }).scrollIntoViewIfNeeded();
}

async function openCommitToast(page: Page, open: (path: string) => Promise<void>) {
  await openSeededReview(page, open);
  await page.getByRole('button', { name: 'Add 2 items' }).click();
  await expect(page.getByText('Added 2 items')).toBeVisible();
  // The celebration dismisses itself; the toast below keeps the words and the Undo.
  await expect(page.getByTestId('commit-celebration')).toBeHidden({ timeout: 10_000 });
  await page.getByRole('button', { name: 'Undo' }).scrollIntoViewIfNeeded();
}

async function openSearchRows(page: Page, open: (path: string) => Promise<void>) {
  await open('/search-rows');
  await expect(page.getByRole('button', { name: 'Alien, Ridley Scott · 1979–2007 · 3 editions, in your library' })).toBeVisible();
  await expect(page.getByText('In your library')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Arrival, Denis Villeneuve · 2016 · 1 edition' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Dune, Frank Herbert · 1990–2019 · 2 editions' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Alien (1979), VHS' })).toBeVisible();
  await expect(page.getByText('You own 1')).toBeVisible();
}

async function openSeededReview(page: Page, open: (path: string) => Promise<void>) {
  await open('/review?seed=review-rows');
  await expect(page.getByText('Pick one · 1')).toBeVisible();
  await expect(page.getByText('Not found · 1')).toBeVisible();
  await expect(page.getByText('You own these · 1')).toBeVisible();
  await expect(page.getByText('Ready · 1')).toBeVisible();
  await expect(page.getByText('Still looking up · 2')).toBeVisible();
  await page.getByRole('button', { name: '1 item ready. Show it' }).click();
  await page.getByRole('button', { name: '1 item you own. Show it' }).click();
  // Row states: the lookup wait names itself, the pick names its next step, the owned row its copy.
  await expect(page.getByText('Identifying…')).toBeVisible();
  await expect(page.getByText('Pick the right one')).toBeVisible();
  await expect(page.getByText(/You own 1/)).toBeVisible();
  // Capture thumbnails pair each picture row with its crop. The Ready row is a barcode scan (#452):
  // no crop, its match preselected, waiting for the commit like every other row.
  await expect(page.getByLabel('Cover of Suddenly You', { exact: true })).toBeVisible();
  await expect(page.getByLabel('Cover of Dune', { exact: true })).toBeVisible();
  await expect(page.getByLabel('Cover of Super Mario 64', { exact: true })).toHaveCount(0);
  // The pick-one editions (#440): radio rows with authors and edition facts, catalog art on the
  // 2001 edition and the captured crop standing in on the 2003, the engine preselection with its
  // reason, the secondary paths as small chips, and Add N counting the selection.
  await expect(page.getByText('Lisa Kleypas · 2001 · Other')).toBeVisible();
  await expect(page.getByText('Lisa Kleypas · 2003 · Other')).toBeVisible();
  await expect(page.getByLabel('Cover of Suddenly You (2001), Other', { exact: true })).toBeVisible();
  await expect(page.getByLabel('Cover of Suddenly You (2003), Other', { exact: true })).toBeVisible();
  await expect(page.getByRole('radio', { name: 'Select Suddenly You (2001), Other' })).toBeVisible();
  await expect(page.getByRole('radio', { name: 'Select Suddenly You (2003), Other' })).toBeChecked();
  await expect(page.getByText('Cover match')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Keep Suddenly You as unverified' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Discard Suddenly You' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Add 2 items' })).toBeVisible();
}
