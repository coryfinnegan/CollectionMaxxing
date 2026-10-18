// Render smoke test: every route in the navigation map renders its screen name.
import { renderRouter, screen } from 'expo-router/testing-library';

const routes: [url: string, expected: string][] = [
  ['/pair', 'Pair with your server'],
  ['/library', 'Library'],
  ['/scan', 'Scan'],
  ['/me', 'Me'],
  ['/me/locations', 'Locations'],
  ['/me/export', 'Export my data'],
  ['/me/devices', 'Devices and browsers'],
  ['/me/delete', 'Delete account'],
  ['/wishlist', 'Wishlist'],
  ['/lent-out', 'Lent out'],
  ['/members', 'Members'],
  ['/settings', 'Settings'],
  ['/about', 'About'],
  ['/item/42', 'Item'],
  ['/item/42/edit', 'Edit item'],
  ['/scanner', 'Scanner'],
  ['/review', 'Review'],
  ['/result/7', 'Result'],
];

describe('routes', () => {
  it.each(routes)('%s renders', async (url, expected) => {
    await renderRouter('./src/app', { initialUrl: url });
    expect((await screen.findAllByText(expected)).length).toBeGreaterThan(0);
  });

  it('opens on the pairing screen at first launch', async () => {
    await renderRouter('./src/app', { initialUrl: '/' });
    expect(await screen.findByText('Pair this iPhone with your library.')).toBeTruthy();
  });
});
