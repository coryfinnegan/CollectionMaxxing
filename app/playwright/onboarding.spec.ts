import { expect, test } from './fixtures';
import { fakeSignIn } from './signin';

// docs/15: every sign-in is an account. `?account=new` opens the mocked app as a brand-new account with no libraries, and
// `?account=signedout` as a browser with no session. After that, specs move around by clicking (a reload resets the mock data).
test.describe('first sign-in', () => {
  test('lands on Welcome, and Create your library makes you its owner', async ({ page, open, shot }) => {
    const signIn = await fakeSignIn(page, '/library?account=new');
    await open('/login');
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();

    await expect(page).toHaveURL(/\/welcome$/);
    expect(signIn.urls).toEqual(['/account/login?returnUrl=%2Flibrary']);
    await expect(page.getByRole('heading', { name: 'Welcome, Ana.' })).toBeVisible();
    await expect(page.getByText("You're signed in with Google as cory@example.com.")).toBeVisible();
    await shot('welcome');

    await page.getByRole('button', { name: 'Create your library' }).click();
    await expect(page.getByRole('textbox', { name: 'Library name' })).toHaveValue("Ana's library");
    await shot('create-library');
    await page.getByRole('textbox', { name: 'Library name' }).fill('Game room');
    await page.getByRole('button', { name: 'Create library' }).click();

    await expect(page).toHaveURL(/\/library$/);
    await expect(page.getByRole('link', { name: 'Welcome' })).toHaveCount(0);
    await page.getByRole('link', { name: 'Members' }).click();
    await expect(page.getByRole('heading', { name: 'Members' })).toBeVisible();
  });

  test('Use another account signs out of Auth0 so the account picker shows again', async ({ page, open }) => {
    await open('/library?account=new');
    await expect(page).toHaveURL(/\/welcome$/);

    await page.getByRole('button', { name: 'Use another account' }).click();

    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByRole('button', { name: 'Sign in', exact: true })).toBeVisible();
  });

  test('the owner limit and a bad name are explained', async ({ page, open }) => {
    await open('/library?account=new');
    for (const name of ['One', 'Two', 'Three']) {
      await page.getByRole('button', { name: 'Create your library' }).or(page.getByRole('link', { name: 'New library' })).click();
      await page.getByRole('textbox', { name: 'Library name' }).fill(name);
      await page.getByRole('button', { name: 'Create library' }).click();
      await expect(page).toHaveURL(/\/library$/);
    }
    await page.getByRole('link', { name: 'New library' }).click();
    await page.getByRole('textbox', { name: 'Library name' }).fill('Four');
    await page.getByRole('button', { name: 'Create library' }).click();

    await expect(page.getByRole('alert')).toHaveText('You already own 3 libraries, which is the most you can have.');
    await expect(page).toHaveURL(/\/welcome\/create$/);
  });
});

test.describe('joining', () => {
  test('by link: previews the invite, then Join lands in the library', async ({ page, open, shot }) => {
    await open('/join/valid-invite?account=new');
    await expect(page.getByRole('heading', { name: "Join Cory's library as Editor" })).toBeVisible();
    await shot('join-link');

    await page.getByRole('button', { name: 'Join', exact: true }).click();

    await expect(page).toHaveURL(/\/library$/);
    await expect(page.getByRole('link', { name: 'Members' })).toBeVisible();
  });

  test('by link, signed out: Sign in to join comes back to the same page', async ({ page, open }) => {
    const signIn = await fakeSignIn(page);
    await open('/join/valid-invite?account=signedout');
    await expect(page.getByRole('heading', { name: "Join Cory's library as Editor" })).toBeVisible();

    await page.getByRole('button', { name: 'Sign in to join' }).click();

    await expect.poll(() => signIn.urls).toEqual(['/account/login?returnUrl=%2Fjoin%2Fvalid-invite']);
  });

  test('by link, a bad invite offers no way in', async ({ page, open }) => {
    await open('/join/expired-invite');
    await expect(page.getByText("That invite doesn't work. Ask for a new one.")).toBeVisible();
    await expect(page.getByRole('button', { name: 'Join', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Sign in to join' })).toHaveCount(0);
  });

  test('by code, typed any way: Welcome, Join a library, check, Join', async ({ page, open, shot }) => {
    await open('/library?account=new');
    await page.getByRole('button', { name: 'Join a library' }).click();
    await expect(page.getByText('Paste the invite link or type the code you were sent.')).toBeVisible();

    await page.getByRole('textbox', { name: 'Invite link or code' }).fill('abcd efgh jk');
    await page.getByRole('button', { name: 'Check invite' }).click();
    await expect(page.getByText("Join Sam's library as Viewer")).toBeVisible();
    await shot('join-code');
    await page.getByRole('button', { name: 'Join', exact: true }).click();

    await expect(page).toHaveURL(/\/library$/);
  });

  test('a code that does not work gets one plain message', async ({ page, open }) => {
    await open('/library?account=new');
    await page.getByRole('button', { name: 'Join a library' }).click();
    await page.getByRole('textbox', { name: 'Invite link or code' }).fill('ZZZZ-ZZZZ-ZZ');
    await page.getByRole('button', { name: 'Check invite' }).click();

    await expect(page.getByRole('alert')).toHaveText("That invite doesn't work. Ask for a new one.");
  });
});

test.describe('library switcher', () => {
  test('hidden with one library; New library adds a second and the switcher moves between them', async ({ page, open, shot }) => {
    await open('/library');
    await expect(page.getByRole('link', { name: 'Open The Matrix' })).toBeVisible();
    await expect(page.getByRole('button', { name: /, Owner$/ })).toHaveCount(0);
    await expect(page.getByRole('link', { name: 'Join a library' })).toBeVisible();

    await page.getByRole('link', { name: 'New library' }).click();
    await page.getByRole('textbox', { name: 'Library name' }).fill('Game room');
    await page.getByRole('button', { name: 'Create library' }).click();
    await expect(page).toHaveURL(/\/library$/);

    const mine = page.getByRole('button', { name: "Cory's library, Owner" });
    const gameRoom = page.getByRole('button', { name: 'Game room, Owner' });
    await expect(gameRoom).toHaveAttribute('aria-current', 'true');
    await shot('switcher');

    await mine.click();
    await expect(mine).toHaveAttribute('aria-current', 'true');
    await expect(gameRoom).not.toHaveAttribute('aria-current', 'true');
  });

  test('joining from the sidebar adds the library with the invited role', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('link', { name: 'Join a library' }).click();
    await page.getByRole('textbox', { name: 'Invite link or code' }).fill('ABCD-EFGH-JK');
    await page.getByRole('button', { name: 'Check invite' }).click();
    await page.getByRole('button', { name: 'Join', exact: true }).click();

    await expect(page.getByRole('button', { name: "Sam's library, Viewer" })).toHaveAttribute('aria-current', 'true');
  });

  test('Leave library (not for owners) ends only your membership', async ({ page, open, shot }) => {
    await open('/library');
    await page.getByRole('link', { name: 'Settings' }).click();
    await expect(page.getByRole('button', { name: 'Leave library' })).toHaveCount(0);

    await page.getByRole('link', { name: 'Join a library' }).click();
    await page.getByRole('textbox', { name: 'Invite link or code' }).fill('ABCD-EFGH-JK');
    await page.getByRole('button', { name: 'Check invite' }).click();
    await page.getByRole('button', { name: 'Join', exact: true }).click();
    await page.getByRole('link', { name: 'Settings' }).click();

    await page.getByRole('button', { name: 'Leave library' }).click();
    await expect(page.getByText("You'll lose access to Sam's library. Your account stays.")).toBeVisible();
    await shot('leave-library');
    await page.getByRole('button', { name: 'Leave library' }).click();

    await expect(page.getByRole('button', { name: /Sam's library/ })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Leave library' })).toHaveCount(0);
  });
});

test.describe('invite code', () => {
  test('is shown with the link when an invite is created', async ({ page, open }) => {
    await open('/members');
    await page.getByRole('button', { name: 'Invite someone' }).click();
    await page.getByRole('textbox', { name: 'Name' }).fill('Taylor');
    await page.getByRole('button', { name: 'Invite as Viewer' }).click();
    await page.getByRole('button', { name: 'Create invite link' }).click();

    await expect(page.getByLabel('Invite link', { exact: true })).toContainText('/join/');
    await expect(page.getByLabel('Invite code')).toHaveText('Or tell them this code: ABCD-EFGH-JK');
  });
});
