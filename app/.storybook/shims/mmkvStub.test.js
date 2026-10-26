// The Storybook MMKV stand-in only selects the callers' memory fallback: stories
// never persist, so the stub refuses the device store instead of bundling it.
import { createMMKV } from './mmkvStub';

describe('storybook mmkv stub', () => {
  it('refuses the device store so the app falls back to memory', () => {
    expect(() => createMMKV({ id: 'display-settings' })).toThrow(/not available on web/);
  });
});
