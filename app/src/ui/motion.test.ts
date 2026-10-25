import { Platform } from 'react-native';
import { maybeEntering, maybeLayout, motionMs, sharedCoverTag, staggerDelay } from './motion';

describe('motion vocabulary', () => {
  it('keeps every duration subtle and fast (150-300ms)', () => {
    for (const ms of Object.values(motionMs)) {
      expect(ms).toBeGreaterThanOrEqual(150);
      expect(ms).toBeLessThanOrEqual(300);
    }
  });

  it('drops entering animations when Reduce Motion is on', () => {
    expect(maybeEntering(true, 'fade')).toBeUndefined();
    expect(maybeEntering(false, 'fade')).toBe('fade');
  });

  it('drops the layout animation when Reduce Motion is on', () => {
    expect(maybeLayout(true)).toBeUndefined();
    expect(maybeLayout(false)).toBeDefined();
  });

  it('staggers grid tiles 20ms apart, capped so a long list never ripples far', () => {
    expect(staggerDelay(0)).toBe(0);
    expect(staggerDelay(3)).toBe(60);
    expect(staggerDelay(8)).toBe(160);
    expect(staggerDelay(40)).toBe(160);
    expect(staggerDelay(-2)).toBe(0);
  });

  it('tags a cover for the shared transition on native without Reduce Motion', () => {
    expect(sharedCoverTag(false, 'item-1')).toBe('cover-item-1');
  });

  it('leaves the cover untagged when Reduce Motion is on', () => {
    expect(sharedCoverTag(true, 'item-1')).toBeUndefined();
  });

  it('leaves the cover untagged on the web, where the plain push is the transition', () => {
    const original = Platform.OS;
    jest.replaceProperty(Platform, 'OS', 'web');
    try {
      expect(sharedCoverTag(false, 'item-1')).toBeUndefined();
    } finally {
      jest.replaceProperty(Platform, 'OS', original);
    }
  });
});
