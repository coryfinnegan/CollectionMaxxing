// The only way app code reaches the native module: `import { visionEngine } from '@/native/cm-vision'`.
// Metro picks index.web.ts on web, so the iOS module is never bundled there.
export * from '../../modules/cm-vision/src';
