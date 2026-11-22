import { createReaderWarmer, WARM_AGAIN_AFTER_MS } from './warmReader';

function setup(opts: { enabled?: boolean; send?: () => Promise<unknown> } = {}) {
  let clock = 1_000_000;
  const send = jest.fn(opts.send ?? (() => Promise.resolve({ status: 202 })));
  const state = { enabled: opts.enabled ?? true };
  const warmer = createReaderWarmer({ enabled: () => state.enabled, send, now: () => clock });
  return { warmer, send, state, advance: (ms: number) => (clock += ms) };
}

describe('warming the shelf reader when the scanner opens', () => {
  it('warms the reader once when the server reads shelves', () => {
    const { warmer, send } = setup();
    warmer.opened();
    expect(send).toHaveBeenCalledTimes(1);
  });

  it('never warms when the server does not read shelves', () => {
    const { warmer, send } = setup({ enabled: false });
    warmer.opened();
    warmer.closed();
    warmer.opened();
    expect(send).not.toHaveBeenCalled();
  });

  it('warms on a later open when the config said the server reads shelves only after the first open', () => {
    const { warmer, send, state } = setup({ enabled: false });
    warmer.opened();
    state.enabled = true;
    warmer.closed();
    warmer.opened();
    expect(send).toHaveBeenCalledTimes(1);
  });

  it('warms once for two opens within five minutes', () => {
    const { warmer, send, advance } = setup();
    warmer.opened();
    warmer.closed();
    advance(WARM_AGAIN_AFTER_MS - 1000);
    warmer.opened();
    expect(send).toHaveBeenCalledTimes(1);
  });

  it('warms again after the scanner has been closed for five minutes', () => {
    const { warmer, send, advance } = setup();
    warmer.opened();
    warmer.closed();
    advance(WARM_AGAIN_AFTER_MS);
    warmer.opened();
    expect(send).toHaveBeenCalledTimes(2);
  });

  it('does not warm again while the scanner stays open, however long', () => {
    const { warmer, send, advance } = setup();
    warmer.opened();
    advance(WARM_AGAIN_AFTER_MS * 3);
    warmer.opened();
    expect(send).toHaveBeenCalledTimes(1);
  });

  it('changes nothing when the warm-up fails', async () => {
    const { warmer, send } = setup({ send: () => Promise.reject(new Error('down')) });
    expect(() => warmer.opened()).not.toThrow();
    await Promise.resolve();
    await Promise.resolve();
    expect(send).toHaveBeenCalledTimes(1);
  });

  it('changes nothing when the warm-up throws before it is sent', () => {
    const { warmer } = setup({
      send: () => {
        throw new Error('boom');
      },
    });
    expect(() => warmer.opened()).not.toThrow();
  });

  it('changes nothing when the recognition config cannot be read', () => {
    const send = jest.fn(() => Promise.resolve());
    const warmer = createReaderWarmer({
      enabled: () => {
        throw new Error('no config');
      },
      send,
      now: () => 0,
    });
    expect(() => warmer.opened()).not.toThrow();
    expect(send).not.toHaveBeenCalled();
  });

  it('does not wait for the warm-up to answer', () => {
    const { warmer } = setup({ send: () => new Promise(() => undefined) });
    expect(warmer.opened()).toBeUndefined();
  });
});
