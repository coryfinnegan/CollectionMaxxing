import { isNewerVersion } from './semver';

describe('isNewerVersion', () => {
  it.each([
    ['1.0.2', '1.0.1'],
    ['1.1.0', '1.0.9'],
    ['2.0.0', '1.99.99'],
    ['1.0.10', '1.0.9'],
    ['10.0.0', '9.9.9'],
  ])('%s is newer than %s', (candidate, current) => {
    expect(isNewerVersion(candidate, current)).toBe(true);
  });

  it.each([
    ['1.0.1', '1.0.2'],
    ['1.0.9', '1.0.10'],
    ['1.0.0', '2.0.0'],
  ])('%s is not newer than %s', (candidate, current) => {
    expect(isNewerVersion(candidate, current)).toBe(false);
  });

  it('is not newer when it is the same version', () => {
    expect(isNewerVersion('1.0.2', '1.0.2')).toBe(false);
  });

  it('ignores a leading v on either side', () => {
    expect(isNewerVersion('v1.0.2', '1.0.1')).toBe(true);
    expect(isNewerVersion('1.0.2', 'v1.0.2')).toBe(false);
  });

  it('puts a release after its own pre-releases', () => {
    expect(isNewerVersion('1.1.0', '1.1.0-rc.1')).toBe(true);
    expect(isNewerVersion('1.1.0-rc.1', '1.1.0')).toBe(false);
  });

  it('orders pre-releases of the same version', () => {
    expect(isNewerVersion('1.1.0-rc.2', '1.1.0-rc.1')).toBe(true);
    expect(isNewerVersion('1.1.0-rc.10', '1.1.0-rc.9')).toBe(true);
    expect(isNewerVersion('1.1.0-rc.1', '1.1.0-beta.5')).toBe(true);
    expect(isNewerVersion('1.1.0-rc.1', '1.1.0-rc.1')).toBe(false);
  });

  it('a pre-release of a later version is still newer', () => {
    expect(isNewerVersion('1.2.0-rc.1', '1.1.0')).toBe(true);
  });

  it.each([null, undefined, '', 'latest', '1.0', '1.0.x', '1.0.2.3', '1.0.2 ', 'one'])('is never newer when the candidate is %p', (candidate) => {
    expect(isNewerVersion(candidate, '1.0.0')).toBe(false);
  });

  it.each([null, undefined, '', 'unknown', '1.0'])('is never newer when this app version is %p, so nobody is nagged about nothing', (current) => {
    expect(isNewerVersion('9.9.9', current)).toBe(false);
  });
});
