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

  // The library on a phone: the Wishlist and Lent out pills under the heading (the wide layout has them in its sidebar).
  test.describe('on a phone', () => {
    test.use({ viewport: { width: 390, height: 844 } });

    test('library phone', async ({ page, open }) => {
      await open('/library');
      await expect(page.getByRole('button', { name: 'Wishlist, 2 items' })).toBeVisible();
      await expect(page.getByRole('button', { name: 'Lent out, 1 item' })).toBeVisible();
      await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toBeVisible();
      await expectShot(page, 'library-phone');
    });
  });

  // What you sold, kept out of the library: the same view with a fixed status and its own empty words.
  test('sold', async ({ page, open }) => {
    await open('/sold');
    await expect(page.getByRole('heading', { name: 'Sold' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Open Tetris' })).toBeVisible();
    await expectShot(page, 'sold');
  });

  // The item's actions sit below the facts, so these two shoot a taller window to show them: the move that fits where the item is
  // (I got it on the wishlist; Lend…, Mark as missing and Sold or gave it away on an owned item), then Edit, Move and Delete.
  test.describe('item actions', () => {
    test.use({ viewport: { width: 1280, height: 1000 } });

    test('item detail wishlist', async ({ page, open }) => {
      await open('/item/c0000000-0000-4000-8000-000000000006');
      await expect(page.getByRole('heading', { name: 'Blade Runner' })).toBeVisible();
      await expect(page.getByRole('button', { name: 'I got it' })).toBeVisible();
      await expectShot(page, 'item-detail-wishlist');
    });

    test('item detail owned', async ({ page, open }) => {
      await open('/library');
      await page.getByRole('link', { name: 'Open The Matrix' }).click();
      await expect(page.getByRole('heading', { name: 'The Matrix' })).toBeVisible();
      await expect(page.getByRole('button', { name: 'Sold or gave it away' })).toBeVisible();
      await expect(page.getByRole('button', { name: 'Edit', exact: true })).toBeVisible();
      await expectShot(page, 'item-detail-owned');
    });
  });

  // The scanner's cards for a release on the wishlist, one you sold, one you own but cannot find, and an ordinary owned one.
  test('scan cards', async ({ page, open }) => {
    await open('/scan-cards');
    await expect(page.getByText('On your wishlist')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Got it: Metroid Prime' })).toBeVisible();
    await expect(page.getByText('You sold this')).toBeVisible();
    await expect(page.getByText('Marked missing')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Found it: Pong' })).toBeVisible();
    // The cards slide in; the shot is of the settled strip.
    await page.waitForTimeout(600);
    await expectShot(page, 'scan-cards');
  });

  test('item detail', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('link', { name: 'Open The Matrix' }).click();
    await expect(page).toHaveURL(new RegExp(`/item/${matrix}$`));
    await expect(page.getByRole('heading', { name: 'The Matrix' })).toBeVisible();
    // The editing buttons wait for the role; the shot is of the finished screen.
    await expect(page.getByRole('button', { name: 'Edit', exact: true })).toBeVisible();
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
    // The sidebar's library list loads on its own clock; wait for it so the snapshot is one state (as members is).
    await expect(page.getByRole('link', { name: 'Join a library' })).toBeVisible();
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

  // A scan's skipped rows (#709): collapsed at the end of Review, then open with what was read, why it was skipped, and Put back.
  test.describe('review skipped seeded', () => {
    test.use({ viewport: { width: 1280, height: 1100 } });

    test('review skipped', async ({ page, open }) => {
      await open('/review?seed=review-skipped');
      await expect(page.getByText('Skipped · 4')).toBeVisible();
      await page.getByRole('button', { name: '4 items skipped. Show them' }).click();
      await expect(page.getByText('Nothing in the catalog matched')).toBeVisible();
      await expect(page.getByText('Read twice')).toBeVisible();
      await expect(page.getByText('Already in your library')).toBeVisible();
      await expect(page.getByText('You skipped it')).toBeVisible();
      await page.getByText('Skipped · 4').scrollIntoViewIfNeeded();
      await expectShot(page, 'review-skipped');
    });
  });

  // Review one at a time on a phone (#833): the seeded rows' two cards that need you, each lit on a drawn shelf photo, then the end.
  // The photo is synthetic (drawn spines, no one's shelf) and goes into the phone's snap store before the app starts.
  test.describe('review walk seeded', () => {
    test.use({ viewport: { width: 390, height: 844 } });

    test('review walk', async ({ page, open }) => {
      await openWalk(page, open);
      await showMatch(page, 'Suddenly You', 0);
      await expectShot(page, 'review-walk');
    });

    test('review walk no cover', async ({ page, open }) => {
      await openWalk(page, open);
      // The engine preselected the 2003 edition, which has no art: the spine strip grows into the cover's place.
      await expect(page.getByLabel(/^Match 2 of 2: Suddenly You/)).toBeVisible();
      await expectShot(page, 'review-walk-nocover');
    });

    test('review walk end', async ({ page, open }) => {
      await openWalk(page, open);
      await page.getByRole('button', { name: 'Add this: Suddenly You' }).click();
      await expect(page.getByText('2 of 2')).toBeVisible();
      await expect(page.getByRole('button', { name: 'Not this one: Unknown item' })).toBeEnabled();
      await page.getByRole('button', { name: 'Not this one: Unknown item' }).click();
      await expect(page.getByText("That's everything that needed you.")).toBeVisible();
      await expect(page.getByText('1 matched by itself.')).toBeVisible();
      await expect(page.getByText('1 you picked.')).toBeVisible();
      await expect(page.getByText('1 you own. Not added.')).toBeVisible();
      await expect(page.getByRole('button', { name: 'Add 2 items' })).toBeVisible();
      await page.waitForTimeout(400);
      await expectShot(page, 'review-walk-end');
    });

    test.describe('dark', () => {
      test.use({ colorScheme: 'dark' });

      test('review walk dark', async ({ page, open }) => {
        await openWalk(page, open);
        await showMatch(page, 'Suddenly You', 0);
        await expectShot(page, 'review-walk-dark');
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

  // The Search view states (#458): the initial view with its recent searches,
  // the loading, results, no-matches, error and offline states, and a work's
  // editions one tap down from the results. The offline state is the
  // scanner's owned-cache answers with the stale line (#459).
  test.describe('search view seeded', () => {
    test.use({ viewport: { width: 1280, height: 1500 } });

    test('search initial', async ({ page, open }) => {
      await open('/search?state=initial');
      await expect(page.getByRole('button', { name: 'Search again for alien' })).toBeVisible();
      await expect(page.getByRole('button', { name: 'Search again for dune' })).toBeVisible();
      await expectShot(page, 'search-initial');
    });

    test('search loading', async ({ page, open }) => {
      await open('/search?state=loading');
      await expect(page.getByText('Searching the catalog')).toBeVisible();
      await expectShot(page, 'search-loading');
    });

    test('search results', async ({ page, open }) => {
      await openSearchResults(page, open);
      await expectShot(page, 'search-results');
    });

    test('search editions', async ({ page, open }) => {
      await openSearchResults(page, open);
      await page.getByRole('button', { name: /^Alien.*Ridley Scott · 1979–2007 · 3 editions.*In your library/ }).click();
      await expect(page.getByRole('button', { name: 'Back to results' })).toBeVisible();
      await expect(page.getByRole('heading', { name: 'Alien' })).toBeVisible();
      await expect(page.getByRole('button', { name: 'Alien (1979), VHS' })).toBeVisible();
      await expectShot(page, 'search-editions');
    });

    test('search no matches', async ({ page, open }) => {
      await open('/search?state=none');
      await expect(page.getByRole('heading', { name: 'No matches' })).toBeVisible();
      await expect(page.getByRole('button', { name: 'Add it yourself' })).toBeVisible();
      await expectShot(page, 'search-none');
    });

    test('search error', async ({ page, open }) => {
      await open('/search?state=error');
      await expect(page.getByRole('heading', { name: "Couldn't search the catalog" })).toBeVisible();
      await expect(page.getByRole('button', { name: 'Try again' })).toBeVisible();
      await expectShot(page, 'search-error');
    });

    test('search offline', async ({ page, open }) => {
      await open('/search?state=offline');
      await expect(page.getByText('Based on your last sync, 2 h ago')).toBeVisible();
      await expect(page.getByText('DVD · Living room › Shelf B')).toBeVisible();
      await expect(page.getByText('Blu-ray · Living room › Shelf B')).toBeVisible();
      await expectShot(page, 'search-offline');
    });

    test.describe('dark', () => {
      test.use({ colorScheme: 'dark' });

      test('search initial dark', async ({ page, open }) => {
        await open('/search?state=initial');
        await expect(page.getByRole('button', { name: 'Search again for alien' })).toBeVisible();
        await expectShot(page, 'search-initial-dark');
      });

      test('search loading dark', async ({ page, open }) => {
        await open('/search?state=loading');
        await expect(page.getByText('Searching the catalog')).toBeVisible();
        await expectShot(page, 'search-loading-dark');
      });

      test('search results dark', async ({ page, open }) => {
        await openSearchResults(page, open);
        await expectShot(page, 'search-results-dark');
      });

      test('search editions dark', async ({ page, open }) => {
        await openSearchResults(page, open);
        await page.getByRole('button', { name: /^Alien.*Ridley Scott · 1979–2007 · 3 editions.*In your library/ }).click();
        await expect(page.getByRole('button', { name: 'Back to results' })).toBeVisible();
        await expectShot(page, 'search-editions-dark');
      });

      test('search no matches dark', async ({ page, open }) => {
        await open('/search?state=none');
        await expect(page.getByRole('heading', { name: 'No matches' })).toBeVisible();
        await expectShot(page, 'search-none-dark');
      });

      test('search error dark', async ({ page, open }) => {
        await open('/search?state=error');
        await expect(page.getByRole('heading', { name: "Couldn't search the catalog" })).toBeVisible();
        await expectShot(page, 'search-error-dark');
      });

      test('search offline dark', async ({ page, open }) => {
        await open('/search?state=offline');
        await expect(page.getByText('Based on your last sync, 2 h ago')).toBeVisible();
        await expect(page.getByText('DVD · Living room › Shelf B')).toBeVisible();
        await expectShot(page, 'search-offline-dark');
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
  // The celebration dismisses itself; the toast below keeps the words and the Undo. Wait to see it first: its entrance can
  // start late on a busy page (Playwright counts it hidden until then), and "hidden" alone would pass before it ever showed.
  await expect(page.getByTestId('commit-celebration')).toBeVisible();
  await expect(page.getByTestId('commit-celebration')).toBeHidden({ timeout: 10_000 });
  await page.getByRole('button', { name: 'Undo' }).scrollIntoViewIfNeeded();
}

async function openSearchResults(page: Page, open: (path: string) => Promise<void>) {
  await open('/search?state=results');
  await expect(page.getByRole('button', { name: /^Alien.*Ridley Scott · 1979–2007 · 3 editions.*In your library/ })).toBeVisible();
  await expect(page.getByText('In your library')).toBeVisible();
  await expect(page.getByRole('button', { name: /Arrival.*Denis Villeneuve · 2016 · 1 edition/ })).toBeVisible();
  await expect(page.getByRole('button', { name: /Dune.*Frank Herbert · 1990–2019 · 2 editions/ })).toBeVisible();
}

async function openSearchRows(page: Page, open: (path: string) => Promise<void>) {
  await open('/search-rows');
  await expect(page.getByRole('button', { name: /^Alien.*Ridley Scott · 1979–2007 · 3 editions.*In your library/ })).toBeVisible();
  await expect(page.getByText('In your library')).toBeVisible();
  await expect(page.getByRole('button', { name: /Arrival.*Denis Villeneuve · 2016 · 1 edition/ })).toBeVisible();
  await expect(page.getByRole('button', { name: /Dune.*Frank Herbert · 1990–2019 · 2 editions/ })).toBeVisible();
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

/**
 * A drawn shelf (no photo of anyone's books): a dozen spines standing on a board, each with its title along it. The seeded picture
 * rows sit on it: the pick (Suddenly You), the not-found read (Dune Mess), the owned Dune and the row still looking up.
 */
const SHELF = { width: 1600, height: 1000, board: 880 };
const SPINES: { title: string; w: number; h: number; fill: string; ink: string; localId?: string }[] = [
  { title: 'Quiet Harbor Almanac', w: 92, h: 760, fill: '#2f4858', ink: '#f4f1ea' },
  { title: 'Lantern Hollow', w: 112, h: 700, fill: '#8a3b2e', ink: '#f6e7c8' },
  { title: 'Suddenly You · Lisa Kleypas', w: 104, h: 800, fill: '#e7d3b0', ink: '#3a2a1a', localId: 'seed-pick' },
  { title: 'Orchard Songs', w: 82, h: 650, fill: '#4a5d3a', ink: '#f4f1ea' },
  { title: 'Dune', w: 124, h: 780, fill: '#c2702b', ink: '#1d1206', localId: 'seed-owned' },
  { title: 'Cedar Row', w: 86, h: 720, fill: '#5b3f6b', ink: '#f0e6f5' },
  { title: 'Dune Mess', w: 106, h: 760, fill: '#1f3d2b', ink: '#e9e2c9', localId: 'seed-lost' },
  { title: 'Marlow Wickersham', w: 96, h: 690, fill: '#7a2f45', ink: '#f4f1ea' },
  { title: 'Spine Row', w: 92, h: 740, fill: '#2b2b38', ink: '#dddddd', localId: 'seed-waiting' },
  { title: 'The Long Field', w: 116, h: 800, fill: '#9a8a5a', ink: '#1a1a12' },
  { title: 'Winter Orchard', w: 82, h: 700, fill: '#36536b', ink: '#f4f1ea' },
  { title: 'Small Hours', w: 102, h: 760, fill: '#b44b3a', ink: '#fff3e0' },
];

function shelfSnap() {
  let x = 70;
  const parts: string[] = [];
  const items: { localId: string; text: string; box: { x: number; y: number; width: number; height: number } }[] = [];
  for (const s of SPINES) {
    const top = SHELF.board - s.h;
    const cx = x + s.w / 2;
    const cy = top + s.h / 2;
    parts.push(
      `<rect x="${x}" y="${top}" width="${s.w}" height="${s.h}" rx="6" fill="${s.fill}"/>`,
      `<rect x="${x}" y="${top + 40}" width="${s.w}" height="10" fill="${s.ink}" opacity="0.35"/>`,
      `<rect x="${x}" y="${SHELF.board - 50}" width="${s.w}" height="10" fill="${s.ink}" opacity="0.35"/>`,
      `<text x="${cx}" y="${cy}" transform="rotate(90 ${cx} ${cy})" text-anchor="middle" dominant-baseline="central" font-family="Georgia, 'DejaVu Serif', serif" font-weight="700" font-size="${Math.round(s.w * 0.42)}" fill="${s.ink}">${s.title}</text>`,
    );
    if (s.localId) items.push({ localId: s.localId, text: s.title, box: { x: x / SHELF.width, y: top / SHELF.height, width: s.w / SHELF.width, height: s.h / SHELF.height } });
    x += s.w + 8;
  }
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${SHELF.width}" height="${SHELF.height}" viewBox="0 0 ${SHELF.width} ${SHELF.height}"><rect width="${SHELF.width}" height="${SHELF.height}" fill="#3b2a1f"/><rect y="${SHELF.board}" width="${SHELF.width}" height="${SHELF.height - SHELF.board}" fill="#6b4a2f"/>${parts.join('')}</svg>`;
  return { snapId: 'seed-snap-1', stillUri: `data:image/svg+xml;base64,${Buffer.from(svg).toString('base64')}`, width: SHELF.width, height: SHELF.height, items };
}

/** Opens the seeded Review on a phone, with the drawn shelf in the phone's snap store as a shelf snap would have left it. */
async function openWalk(page: Page, open: (path: string) => Promise<void>) {
  const snap = shelfSnap();
  await page.addInitScript((stored) => {
    // react-native-mmkv on the web keeps each instance's keys in localStorage as `<id>\<key>`.
    localStorage.setItem('snap-store\\snap-store-v1', JSON.stringify([{ ...stored, createdAt: Date.now() }]));
  }, snap);
  await open('/review?seed=review-rows');
  await expect(page.getByText('1 of 2')).toBeVisible();
  await expect(page.getByTestId('walk-band')).toBeVisible();
  // The band glides and the card comes in; the shot is of the settled card.
  await page.waitForTimeout(600);
}

/** Pages the match reel to one match, as a swipe would. */
async function showMatch(page: Page, title: string, index: number) {
  await page.getByTestId('match-pages').evaluate((el, i) => el.scrollTo({ left: i * el.clientWidth, behavior: 'instant' as ScrollBehavior }), index);
  await expect(page.getByLabel(new RegExp(`^Match ${index + 1} of \\d+: ${title}`))).toBeVisible();
  await page.waitForTimeout(300);
}
