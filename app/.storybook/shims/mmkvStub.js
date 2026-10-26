// Storybook-only react-native-mmkv stand-in. The real module pulls
// react-native-nitro-modules (Flow-typed react-native internals) into the Vite
// bundle, which cannot parse them. Every app call site opens MMKV inside
// try/catch and falls back to an in-memory store, so throwing here only
// selects the fallback: stories render with memory stores and lose nothing.
export function createMMKV() {
  throw new Error('[Storybook] react-native-mmkv is not available on web');
}
