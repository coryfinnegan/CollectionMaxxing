import fs from 'node:fs';
import path from 'node:path';

const mockNativeOnly = jest.fn(() => {
  throw new Error('native code was imported on web');
});
jest.mock('expo', () => ({ requireNativeModule: mockNativeOnly }));
jest.mock('react-native-vision-camera', () => mockNativeOnly());

describe('index.web', () => {
  it('has no import of native code', () => {
    const source = fs.readFileSync(path.join(__dirname, '..', 'index.web.ts'), 'utf8');
    const imports = [...source.matchAll(/from\s+'([^']+)'/g)].map((m) => m[1]);
    for (const spec of imports) {
      expect(spec).toMatch(/^\.\//);
      expect(spec).not.toMatch(/native|Native/);
    }
  });

  it('loads without touching native modules and exposes an unavailable engine', async () => {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const web = require('../index.web');
    expect(mockNativeOnly).not.toHaveBeenCalled();
    await expect(web.visionEngine.isAvailable()).resolves.toBe(false);
  });

  it('says it is unavailable on every recognition call', async () => {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { visionEngine } = require('../index.web');
    await expect(visionEngine.recognizeText('x')).rejects.toThrow(/not available on the web/);
    await expect(visionEngine.detectRectangles('x')).rejects.toThrow(/not available on the web/);
    await expect(visionEngine.detectBarcodes('x')).rejects.toThrow(/not available on the web/);
    await expect(visionEngine.featurePrint('x')).rejects.toThrow(/not available on the web/);
    await expect(
      visionEngine.featurePrintDistance({ revision: 1, data: '' }, { revision: 1, data: '' }),
    ).rejects.toThrow(/not available on the web/);
  });

  it('reports the model as unavailable and resolves a read with the unavailable outcome', async () => {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { visionEngine } = require('../index.web');
    await expect(visionEngine.modelCapabilities()).resolves.toMatchObject({ available: false, vision: false, reason: 'notSupported' });
    const onItem = jest.fn();
    await expect(visionEngine.readItems('x', { prompt: 'p', timeoutMs: 1000 }, onItem)).resolves.toMatchObject({
      items: [],
      outcome: 'unavailable',
    });
    expect(onItem).not.toHaveBeenCalled();
  });
});
