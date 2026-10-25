// The live camera (iOS). Frames go to the Apple decoder through cm-vision; the codes it finds are handed to the
// source. This file is only loaded on a phone: the web build uses CameraPreview.web.tsx, which has no camera.
import { useEffect, useRef, useState } from 'react';
import { StyleSheet, View } from 'react-native';
import { Camera, useCameraDevice, useCameraPermission, usePhotoOutput } from 'react-native-vision-camera';
import { useBarcodeFrameProcessor } from '@/native/cm-vision';
import { fixed, spacing } from '@/theme/tokens';
import { trackPicture } from '../pictureFiles';
import { Button, Text } from '@/ui';
import type { VisionCameraSource } from './ScannerSource';
import { createSilentSnapshot } from './silentSnapshot';

export default function CameraPreview({ source }: { source: VisionCameraSource }) {
  const { hasPermission, requestPermission } = useCameraPermission();
  const device = useCameraDevice('back');
  const [torch, setTorch] = useState(source.torch);
  useEffect(() => source.onTorch(setTorch), [source]);
  const photoOutput = usePhotoOutput();
  // The shutter and auto-snap take their still here. The picture stays on the phone (docs/02 "Privacy").
  const capturing = useRef(0);
  useEffect(() => {
    // Auto-snap's low-rate look: a silent, small still that is deleted after Vision reads it (silentSnapshot.ts).
    const silent = createSilentSnapshot({
      isCapturing: () => capturing.current > 0,
      take: async () => {
        // In memory first: the Photo knows its size and orientation; only then is it written to the temp folder.
        const photo = await photoOutput.capturePhoto({ flashMode: 'off', enableShutterSound: false }, {});
        try {
          const path = await photo.saveToTemporaryFileAsync();
          return { uri: toFileUri(path), width: photo.width, height: photo.height, orientation: photo.orientation };
        } finally {
          photo.dispose();
        }
      },
      shrink: async (uri, size) => {
        const { ImageManipulator, SaveFormat } = await import('expo-image-manipulator');
        const image = await ImageManipulator.manipulate(uri).resize(size).renderAsync();
        return image.saveAsync({ format: SaveFormat.JPEG, compress: 0.6 });
      },
      remove: async (uri) => {
        const { File } = await import('expo-file-system');
        const file = new File(uri);
        if (file.exists) file.delete();
      },
      // Only the temp and cache folders; never the app's documents.
      isTemp: (uri) => !/\/Documents\//i.test(uri) && /\/(tmp|Caches)\//i.test(uri),
    });
    // The shutter and auto-snap take their still here. The picture stays on the phone (docs/02 "Privacy").
    source.attachCamera({
      capture: async () => {
        capturing.current += 1;
        try {
          const file = await photoOutput.capturePhotoToFile({ flashMode: 'off' }, {});
          // Width and height are read when the picture is scaled (snapPhoto); the camera's EXIF orientation is applied there.
          // Registered at once, so a clear finds the capture even before it has been scaled (snapPhoto deletes it after).
          return { uri: trackPicture(toFileUri(file.filePath)), width: 0, height: 0, orientation: 1 };
        } finally {
          capturing.current -= 1;
        }
      },
      snapshot: silent.take,
      releaseSnapshot: silent.release,
    });
    return () => source.attachCamera(undefined);
  }, [source, photoOutput]);
  const output = useBarcodeFrameProcessor((codes) =>
    source.feed(codes.map((c) => ({ value: c.value, symbology: c.symbology }))),
  );

  if (!hasPermission) {
    return (
      <View style={styles.message}>
        <Text style={styles.onCamera}>CollectionMaxxing needs the camera to read barcodes.</Text>
        <Button label="Allow camera" onPress={() => void requestPermission()} />
      </View>
    );
  }
  if (!device) {
    return (
      <View style={styles.message}>
        <Text style={styles.onCamera}>No camera found on this device.</Text>
      </View>
    );
  }
  return <Camera style={StyleSheet.absoluteFill} device={device} isActive outputs={[output, photoOutput]} torchMode={torch ? 'on' : 'off'} />;
}

const toFileUri = (path: string) => (path.startsWith('file://') ? path : `file://${path}`);

const styles = StyleSheet.create({
  message: { position: 'absolute', top: 0, left: 0, right: 0, bottom: 0, alignItems: 'center', justifyContent: 'center', gap: spacing.md, padding: spacing.xl },
  onCamera: { color: fixed.white, textAlign: 'center' },
});
