// LOCKED acceptance test for CAM-3d (issue #120). Never weaken, skip or delete it.
import { waitFor } from '@testing-library/react-native';
import { identifyMock } from '@/mocks/identify';
import { scanMock } from '@/mocks/scan';
import { useCardsStore } from '../cards/cardsStore';
import { DEV_CODES } from '../scanner/devCodes';
import { makeKit, readItem, readingOf, recordRequests, resetKit } from '../pipeline/testKit';
import { code, importKit } from './importKit';

jest.mock('expo-router', () => ({ useRouter: () => ({ push: jest.fn(), back: jest.fn(), replace: jest.fn() }) }));

beforeEach(() => {
  scanMock.reset();
  identifyMock.reset();
  useCardsStore.getState().reset();
});
afterEach(() => {
  resetKit();
  scanMock.reset();
});

describe('no image data leaves the phone on the import path', () => {
  it('sends only JSON text and fields across a 5-image import', async () => {
    const kit = makeKit();
    const { picker, barcodes, open, pressPhotos } = importKit(kit);
    const uris = picker.images(5);
    barcodes.codes.set(uris[2] as string, [code(DEV_CODES.duneOwned)]); // one of the five is a barcode picture
    kit.reader.script(
      readingOf([readItem('Space Mutiny', { kind: 'film', format: 'vhs' })]),
      readingOf([readItem('Super Mario 64', { kind: 'game', platform: 'Nintendo 64' })]),
      readingOf([readItem('Zelda: Ocarina of Time', { kind: 'game', platform: 'Nintendo 64' })]),
      readingOf([readItem('Ghostbusters', { kind: 'film' })]),
    );
    const requests = recordRequests();
    await open();

    await pressPhotos();
    await waitFor(() => expect(kit.reader.stills).toHaveLength(4));
    await waitFor(() => expect(identifyMock.requests).toHaveLength(4));
    // The barcode picture's answer, from the barcode path (queued silently for Review since #432; the privacy
    // assertions below are unchanged).
    await waitFor(() => expect(Object.values(useCardsStore.getState().byId).some((c) => c.release?.title === 'Dune')).toBe(true));

    const sent = (await requests.all()).filter((r) => r.method !== 'GET');
    requests.stop();
    expect(sent.length).toBeGreaterThan(0);
    for (const request of sent) {
      expect(request.contentType ?? '').toMatch(/^application\/json/); // never multipart, never bytes
      expect(request.body).not.toMatch(/data:|file:|content:|base64|\.jpe?g|\.heic|\.png|ImagePicker/i);
      expect(() => JSON.parse(request.body || '{}')).not.toThrow();
    }
    expect(sent.some((r) => /\/photo$/.test(new URL(r.url).pathname))).toBe(false);
    expect(identifyMock.requests.every((r) => r.body.trigger === 'import')).toBe(true);
  });
});
