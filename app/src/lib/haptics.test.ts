import { type HapticDriver, haptics, setHapticDriver } from './haptics';

const fake = () => {
  const calls: string[] = [];
  const driver: HapticDriver = {
    tap: () => void calls.push('tap'),
    longTap: () => void calls.push('long'),
    tick: () => void calls.push('tick'),
    wait: async () => void calls.push('wait'),
  };
  setHapticDriver(driver);
  return calls;
};

afterEach(() => setHapticDriver(null));

describe('haptics', () => {
  it('gives two short taps for something you own', async () => {
    const calls = fake();
    await haptics.owned();
    expect(calls).toEqual(['tap', 'wait', 'tap']);
  });

  it('gives one tap for something new', async () => {
    const calls = fake();
    await haptics.isNew();
    expect(calls).toEqual(['tap']);
  });

  it('gives one long tap for a code the catalogue does not know', async () => {
    const calls = fake();
    await haptics.notFound();
    expect(calls).toEqual(['long']);
  });

  it('ticks as soon as a code is read', async () => {
    const calls = fake();
    await haptics.scanned();
    expect(calls).toEqual(['tick']);
  });

  it('keeps going when the device cannot vibrate', async () => {
    setHapticDriver({
      tap: () => Promise.reject(new Error('no motor')),
      longTap: () => undefined,
      tick: () => undefined,
      wait: async () => undefined,
    });
    await expect(haptics.owned()).resolves.toBeUndefined();
  });
});
