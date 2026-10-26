// Sharing a Great Hall milestone (#363): the native sheet on the iPhone, the Web
// Share sheet where the browser has one, else a clipboard copy.
import { Platform } from 'react-native';
import { shareMilestone } from './shareMilestone';

describe('shareMilestone', () => {
  it('shares through the native sheet off the web', async () => {
    jest.replaceProperty(Platform, 'OS', 'ios');
    const nativeShare = jest.fn().mockResolvedValue(undefined);
    expect(await shareMilestone('I shelved 100 items', { nativeShare })).toBe('shared');
    expect(nativeShare).toHaveBeenCalledWith('I shelved 100 items');
  });

  it('shares through Web Share on the web where the browser has it', async () => {
    jest.replaceProperty(Platform, 'OS', 'web');
    const webShare = jest.fn().mockResolvedValue(undefined);
    const copyText = jest.fn().mockResolvedValue(undefined);
    expect(await shareMilestone('I shelved 100 items', { nativeShare: jest.fn(), webShare, copyText })).toBe('shared');
    expect(webShare).toHaveBeenCalledWith('I shelved 100 items');
    expect(copyText).not.toHaveBeenCalled();
  });

  it('copies to the clipboard on the web without Web Share', async () => {
    jest.replaceProperty(Platform, 'OS', 'web');
    const copyText = jest.fn().mockResolvedValue(undefined);
    expect(await shareMilestone('I shelved 100 items', { nativeShare: jest.fn(), copyText })).toBe('copied');
    expect(copyText).toHaveBeenCalledWith('I shelved 100 items');
  });

  it('says when the web has no channel at all', async () => {
    jest.replaceProperty(Platform, 'OS', 'web');
    expect(await shareMilestone('I shelved 100 items', { nativeShare: jest.fn() })).toBe('unavailable');
  });
});
