// The live camera asks for permission while it can still be asked; when access is denied or restricted it explains
// and links to Settings instead of dead-ending.
import { fireEvent, render, screen } from '@testing-library/react-native';
import { Linking } from 'react-native';
import { useCameraDevice, useCameraPermission } from 'react-native-vision-camera';
import { renderWithProviders } from '@/test/render';
import CameraPreview from './CameraPreview';
import type { VisionCameraSource } from './ScannerSource';

const mockCamera = jest.fn((_props: { isActive: boolean }) => null);
jest.mock('react-native-vision-camera', () => ({
  Camera: (props: { isActive: boolean }) => mockCamera(props),
  useCameraDevice: jest.fn(),
  useCameraPermission: jest.fn(),
  usePhotoOutput: () => ({}),
}));
jest.mock('@/native/cm-vision', () => ({ useBarcodeFrameProcessor: () => ({}) }));

const permission = useCameraPermission as jest.Mock;
const device = useCameraDevice as jest.Mock;
// The preview only attaches to the source; no picture is ever taken here.
const source = { flash: false, onFlash: () => () => undefined, attachCamera: () => undefined, feed: () => undefined } as unknown as VisionCameraSource;

const grant = (status: string, requestPermission = jest.fn()) => {
  permission.mockReturnValue({ status, hasPermission: status === 'authorized', canRequestPermission: status === 'not-determined', requestPermission });
  return requestPermission;
};

beforeEach(() => {
  device.mockReturnValue({ id: 'back' });
  mockCamera.mockClear();
});

describe('camera permission', () => {
  it('asks for the camera while it can still be asked', async () => {
    const requestPermission = grant('not-determined');
    await renderWithProviders(<CameraPreview source={source} />);

    fireEvent.press(await screen.findByLabelText('Allow camera'));
    expect(requestPermission).toHaveBeenCalled();
  });

  it('explains a denied camera and links to Settings', async () => {
    grant('denied');
    const settings = jest.spyOn(Linking, 'openSettings').mockResolvedValue(undefined);
    try {
      await renderWithProviders(<CameraPreview source={source} />);

      expect(await screen.findByText('Camera access is off. Turn it on in Settings to scan.')).toBeTruthy();
      fireEvent.press(screen.getByLabelText('Open Settings'));
      expect(settings).toHaveBeenCalled();
    } finally {
      settings.mockRestore();
    }
  });

  it('explains a restricted camera and links to Settings', async () => {
    grant('restricted');
    const settings = jest.spyOn(Linking, 'openSettings').mockResolvedValue(undefined);
    try {
      await renderWithProviders(<CameraPreview source={source} />);

      expect(await screen.findByText('The camera is restricted on this iPhone. Allow it in Settings to scan.')).toBeTruthy();
      fireEvent.press(screen.getByLabelText('Open Settings'));
      expect(settings).toHaveBeenCalled();
    } finally {
      settings.mockRestore();
    }
  });

  it('shows the camera once allowed', async () => {
    grant('authorized');
    await renderWithProviders(<CameraPreview source={source} />);

    expect(screen.queryByLabelText('Allow camera')).toBeNull();
    expect(screen.queryByText('No camera found on this device.')).toBeNull();
  });

  it('says when the device has no camera', async () => {
    grant('authorized');
    device.mockReturnValue(undefined);
    await renderWithProviders(<CameraPreview source={source} />);

    expect(await screen.findByText('No camera found on this device.')).toBeTruthy();
  });
});

describe('the camera is only running while the scanner is on screen', () => {
  const running = () => mockCamera.mock.calls.at(-1)?.[0].isActive;

  it('runs while the scanner is active', async () => {
    grant('authorized');
    await renderWithProviders(<CameraPreview source={source} active />);
    expect(running()).toBe(true);
  });

  it('is off when the scanner is not active, and on again when it is', async () => {
    grant('authorized');
    const { rerender } = await render(<CameraPreview source={source} active={false} />);
    expect(running()).toBe(false);

    await rerender(<CameraPreview source={source} active />);
    expect(running()).toBe(true);
  });
});
