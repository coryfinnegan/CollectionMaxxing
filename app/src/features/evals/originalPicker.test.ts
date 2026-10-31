import { deviceOriginalPicker, originalPickOptions } from './originalPicker';

const mockRequest = jest.fn();
const mockLaunch = jest.fn();

jest.mock('expo-image-picker', () => ({
  requestMediaLibraryPermissionsAsync: (...args: unknown[]) => mockRequest(...args),
  launchImageLibraryAsync: (...args: unknown[]) => mockLaunch(...args),
  UIImagePickerPreferredAssetRepresentationMode: { Automatic: 'automatic', Compatible: 'compatible', Current: 'current' },
}));

// The picture must go to the PC as the camera made it: the whole flow may never use the image manipulator.
jest.mock('expo-image-manipulator', () => {
  throw new Error('The original must never be run through expo-image-manipulator.');
});

beforeEach(() => {
  mockRequest.mockReset().mockResolvedValue({ granted: true });
  mockLaunch.mockReset();
});

describe('the picker options that keep the original', () => {
  it('ask for the file as it is: no compression, no editing, no transcoding, iCloud originals downloaded', async () => {
    mockLaunch.mockResolvedValue({ canceled: true, assets: null });

    await deviceOriginalPicker.pick();

    expect(mockLaunch).toHaveBeenCalledTimes(1);
    expect(mockLaunch).toHaveBeenCalledWith({
      mediaTypes: ['images'],
      allowsMultipleSelection: true,
      orderedSelection: true,
      allowsEditing: false,
      quality: 1,
      exif: false,
      base64: false,
      preferredAssetRepresentationMode: 'current', // not 'compatible' (a JPEG made for sharing) and not 'automatic' (the system decides)
      shouldDownloadFromNetwork: true,
    });
  });

  it('never ask for a size: there is no resize, max width or selection cap in the options', () => {
    const options = originalPickOptions({ UIImagePickerPreferredAssetRepresentationMode: { Automatic: 'a', Compatible: 'c', Current: 'x' } } as never);

    expect(Object.keys(options).sort()).toEqual([
      'allowsEditing',
      'allowsMultipleSelection',
      'base64',
      'exif',
      'mediaTypes',
      'orderedSelection',
      'preferredAssetRepresentationMode',
      'quality',
      'shouldDownloadFromNetwork',
    ]);
    expect(options.quality).toBe(1);
    expect(options.preferredAssetRepresentationMode).toBe('x');
  });
});

describe('the device picker', () => {
  it('hands back each picked original with its address, size in pixels, name and type', async () => {
    mockLaunch.mockResolvedValue({
      canceled: false,
      assets: [
        { uri: 'file:///cache/IMG_1.HEIC', width: 4032, height: 3024, fileName: 'IMG_1.HEIC', mimeType: 'image/heic', fileSize: 3_100_000 },
        { uri: 'file:///cache/IMG_2.JPG', width: 2576, height: 1932 },
      ],
    });

    const result = await deviceOriginalPicker.pick();

    expect(result).toEqual({
      status: 'picked',
      photos: [
        { uri: 'file:///cache/IMG_1.HEIC', width: 4032, height: 3024, fileName: 'IMG_1.HEIC', mimeType: 'image/heic', fileSize: 3_100_000 },
        { uri: 'file:///cache/IMG_2.JPG', width: 2576, height: 1932, fileName: null, mimeType: null, fileSize: null },
      ],
    });
  });

  it('says cancelled when the owner backs out', async () => {
    mockLaunch.mockResolvedValue({ canceled: true, assets: null });

    expect(await deviceOriginalPicker.pick()).toEqual({ status: 'cancelled' });
  });

  it('says denied, and opens nothing, when photo access is off', async () => {
    mockRequest.mockResolvedValue({ granted: false, accessPrivileges: 'none' });

    expect(await deviceOriginalPicker.pick()).toEqual({ status: 'denied' });
    expect(mockLaunch).not.toHaveBeenCalled();
  });

  it('opens the picker when access is limited to the photos the owner chose', async () => {
    mockRequest.mockResolvedValue({ granted: false, accessPrivileges: 'limited' });
    mockLaunch.mockResolvedValue({ canceled: true, assets: null });

    await deviceOriginalPicker.pick();

    expect(mockLaunch).toHaveBeenCalledTimes(1);
  });
});
