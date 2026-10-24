import { loginUrl, safeReturnUrl, signInErrorMessage } from './navigation';

describe('loginUrl', () => {
  it('is the bare sign-in route with no options', () => {
    expect(loginUrl()).toBe('/account/login');
  });

  it.each([
    [{ returnUrl: '/members?x=1' }, '/account/login?returnUrl=%2Fmembers%3Fx%3D1'],
    [{ returnUrl: '/join/tok' }, '/account/login?returnUrl=%2Fjoin%2Ftok'],
    [{ reauth: true, returnUrl: '/me/delete' }, '/account/login?returnUrl=%2Fme%2Fdelete&reauth=1'],
    [{ link: true }, '/account/login?link=1'],
  ])('builds %j', (options, expected) => {
    expect(loginUrl(options)).toBe(expected);
  });
});

describe('safeReturnUrl', () => {
  it.each(['/library', '/members?x=1'])('keeps the local path %s', (path) => {
    expect(safeReturnUrl(path)).toBe(path);
  });

  it.each([undefined, '', 'library', 'https://elsewhere.example/', '//elsewhere.example', '/\\elsewhere.example', 42])(
    'falls back for %p',
    (value) => {
      expect(safeReturnUrl(value)).toBe('/library');
    },
  );
});

describe('signInErrorMessage', () => {
  it('has copy for every code the server redirects with, and none for others', () => {
    for (const code of ['signin_failed', 'invite_invalid', 'already_linked', 'reauth_failed']) {
      expect(signInErrorMessage(code)).toBeTruthy();
    }
    expect(signInErrorMessage('unknown')).toBeNull();
    expect(signInErrorMessage(undefined)).toBeNull();
  });
});
