// The live camera (iOS). Frames go to the Apple decoder through cm-vision; the codes it finds are handed to the
// source. This file is only loaded on a phone: the web build uses CameraPreview.web.tsx, which has no camera.
import { useEffect, useState } from 'react';
import { StyleSheet, View } from 'react-native';
import { Camera, useCameraDevice, useCameraPermission, usePhotoOutput } from 'react-native-vision-camera';
import { useBarcodeFrameProcessor } from '@/native/cm-vision';
import { spacing } from '@/theme/tokens';
import { Button, Text } from '@/ui';
import type { VisionCameraSource } from './ScannerSource';

export default function CameraPreview({ source }: { source: VisionCameraSource }) {
  const { hasPermission, requestPermission } = useCameraPermission();
  const device = useCameraDevice('back');
  const [torch, setTorch] = useState(source.torch);
  useEffect(() => source.onTorch(setTorch), [source]);
  const photoOutput = usePhotoOutput();
  // The shutter and auto-snap take their still here. The picture stays on the phone (docs/02 "Privacy").
  useEffect(() => {
    source.attachCamera({
      capture: async () => {
        const file = await photoOutput.capturePhotoToFile({ flashMode: 'off' }, {});
        const uri = file.filePath.startsWith('file://') ? file.filePath : `file://${file.filePath}`;
        // Width and height are read when the picture is scaled (snapPhoto); the camera's EXIF orientation is applied there.
        return { uri, width: 0, height: 0, orientation: 1 };
      },
      // Not wired yet (see the PR notes): a silent low-resolution snapshot source. Until then no signals flow and only the shutter snaps.
      snapshot: async () => null,
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

const styles = StyleSheet.create({
  message: { position: 'absolute', top: 0, left: 0, right: 0, bottom: 0, alignItems: 'center', justifyContent: 'center', gap: spacing.md, padding: spacing.xl },
  onCamera: { color: '#FFFFFF', textAlign: 'center' },
});
