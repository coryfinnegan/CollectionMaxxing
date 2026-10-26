// The only way app code reaches the native module: `import { appIcon } from '@/native/cm-app-icon'`.
// Metro picks index.web.ts on web, so the iOS module is never bundled there.
export * from '../../modules/cm-app-icon/src';
