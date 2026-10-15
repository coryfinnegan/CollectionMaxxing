import { expect, test } from './fixtures';

test.describe('members and join', () => {
  test('inviting shows the link once, and a second browser joins from it', async ({ page, open, browser, baseURL, shot }) => {
    await open('/library');
    await page.getByRole('link', { name: 'Members' }).click();
    await expect(page).toHaveURL(/\/members$/);
    await expect(page.getByText('Cory (you)')).toBeVisible();
    await shot('members');

    await page.getByRole('button', { name: 'Invite someone' }).click();
    await page.getByRole('textbox', { name: 'Name' }).fill('Taylor');
    await page.getByRole('button', { name: 'Invite as Viewer' }).click();
    await page.getByRole('button', { name: 'Create invite link' }).click();

    const link = page.getByLabel('Invite link', { exact: true });
    await expect(link).toContainText('/join/');
    await expect(page.getByText("It won't be shown again")).toBeVisible();
    await shot('invite-link');

    await page.getByRole('button', { name: 'Close invite' }).click();
    await expect(page.getByText('Taylor · Viewer')).toBeVisible();
    // The list never carries the link again.
    await expect(page.getByLabel('Invite link', { exact: true })).toHaveCount(0);

    // A second, separate browser context opens the invite and joins. The mock only knows the token "valid-invite".
    const other = await browser.newContext({ baseURL });
    const guest = await other.newPage();
    await guest.goto('/join/valid-invite');
    await expect(guest.getByRole('heading', { name: "Join Cory's library as Editor" })).toBeVisible();
    await guest.getByRole('textbox', { name: 'Your name' }).fill('Taylor');
    await guest.getByRole('button', { name: 'Create passkey' }).click();
    await expect(guest).toHaveURL(/\/library$/);
    await other.close();
  });

  test('an unknown invite link says it is not valid, without saying why', async ({ browser, baseURL }) => {
    const other = await browser.newContext({ baseURL });
    const guest = await other.newPage();
    await guest.goto('/join/not-a-real-token');
    await expect(guest.getByText("This invite link isn't valid anymore. Ask for a new one.")).toBeVisible();
    await other.close();
  });

  test('a role change shows who it applies to', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('link', { name: 'Members' }).click();
    await page.getByRole('button', { name: 'Role for Robin: Editor' }).click();
    await expect(page.getByText('Robin is now an Editor.')).toBeVisible();
  });

  test('removing a member asks first and then removes them', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('link', { name: 'Members' }).click();
    await page.getByRole('button', { name: 'Remove Sam' }).click();
    await expect(page.getByText('Remove Sam?')).toBeVisible();
    await page.getByRole('button', { name: 'Remove Sam and sign them out' }).click();
    await expect(page.getByText('Sam was removed and signed out everywhere.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Role for Sam: Editor' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Role for Robin: Editor' })).toBeVisible();
  });

  test('revoking a pending invite removes it from the list', async ({ page, open }) => {
    await open('/library');
    await page.getByRole('link', { name: 'Members' }).click();
    await page.getByRole('button', { name: 'Revoke invite for Alex' }).click();
    await expect(page.getByRole('button', { name: 'Revoke invite for Alex' })).toHaveCount(0);
  });
});
