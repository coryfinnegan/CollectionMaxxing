import { deviceInfo } from './device';

jest.mock('expo-constants', () => ({
  __esModule: true,
  default: { platform: { ios: { model: 'iPhone16,1' } }, expoConfig: { version: '1.2.3', ios: { buildNumber: '1' } } },
}));

describe('deviceInfo', () => {
  it('carries no build number even when the expo config has one (it is "1" on TestFlight; the native device status has the real one)', () => {
    expect(deviceInfo()).toEqual({ model: 'iPhone16,1', appVersion: '1.2.3' });
  });
});
