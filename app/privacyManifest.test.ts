import fs from 'fs';
import path from 'path';
import { APPLE_REASON_CODES, privacyManifest } from './privacyManifest';

describe('iOS privacy manifest', () => {
  it('says the app does not track', () => {
    expect(privacyManifest.NSPrivacyTracking).toBe(false);
    expect(privacyManifest.NSPrivacyTrackingDomains).toEqual([]);
  });

  it('collects no data types', () => {
    expect(privacyManifest.NSPrivacyCollectedDataTypes).toEqual([]);
  });

  it('gives every required-reason API a category and at least one valid reason code', () => {
    expect(privacyManifest.NSPrivacyAccessedAPITypes.length).toBeGreaterThan(0);
    for (const entry of privacyManifest.NSPrivacyAccessedAPITypes) {
      const valid = APPLE_REASON_CODES[entry.NSPrivacyAccessedAPIType];
      expect(valid).toBeDefined();
      expect(entry.NSPrivacyAccessedAPITypeReasons.length).toBeGreaterThan(0);
      for (const code of entry.NSPrivacyAccessedAPITypeReasons) expect(valid).toContain(code);
    }
  });

  it('lists each category once', () => {
    const types = privacyManifest.NSPrivacyAccessedAPITypes.map((e) => e.NSPrivacyAccessedAPIType);
    expect(new Set(types).size).toBe(types.length);
  });
});

describe('iOS permission strings in app.config.ts', () => {
  const source = fs.readFileSync(path.join(__dirname, 'app.config.ts'), 'utf8');

  it('explains why the camera is used', () => {
    expect(source).toMatch(/NSCameraUsageDescription:\s*'[^']{20,}'/);
  });

  it('explains why the photo library is read', () => {
    expect(source).toMatch(/photosPermission:\s*'[^']{20,}'/);
  });
});
