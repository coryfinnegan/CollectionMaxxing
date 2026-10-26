import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { StorybookConfig } from '@storybook/react-vite';

const here = dirname(fileURLToPath(import.meta.url));

const config: StorybookConfig = {
  stories: ['../src/ui/*.stories.tsx'],
  addons: [],
  framework: { name: '@storybook/react-vite', options: {} },
  viteFinal: async (viteConfig, options) => {
    const dev = options?.configType === 'DEVELOPMENT';
    // Metro defines globals that RN libraries reference; mirror them per mode.
    viteConfig.define ??= {};
    viteConfig.define.__DEV__ = dev ? 'true' : 'false';
    viteConfig.define.process = JSON.stringify({ env: { NODE_ENV: dev ? 'development' : 'production' } });
    viteConfig.resolve ??= {};
    const existing = viteConfig.resolve.alias;
    const entries = Array.isArray(existing)
      ? [...existing]
      : Object.entries(existing ?? {}).map(([find, replacement]) => ({ find, replacement }));
    entries.push(
      // The app's web path: RN primitives render through react-native-web,
      // plus the RN-only module-registry surface (see the compat shim).
      { find: /^react-native$/, replacement: join(here, 'shims/reactNativeWebCompat.js') },
      // Reanimated's worklet runtime is Metro-only; stories show static
      // primitives, so they use the Jest mock's ESM twin (see the shim).
      { find: /^react-native-reanimated$/, replacement: join(here, 'shims/reanimatedMock.js') },
      // expo-haptics ships TS source Vite cannot bundle; the app never calls
      // it on web (see the stub), so stories lose nothing.
      { find: /^expo-haptics$/, replacement: join(here, 'shims/expoHapticsStub.js') },
      // react-native-mmkv pulls nitro plus Flow-typed RN internals that Vite
      // cannot parse; stories never persist (the stub throws, so every call
      // site falls back to its memory store), so stories lose nothing.
      { find: /^react-native-mmkv$/, replacement: join(here, 'shims/mmkvStub.js') },
      { find: /^@\//, replacement: `${join(here, '..', 'src')}/` },
    );
    viteConfig.resolve.alias = entries;
    return viteConfig;
  },
};

export default config;
