// Sharing a Great Hall milestone (#363): the native share sheet on the iPhone,
// the Web Share sheet where the browser has one, else a clipboard copy.
import { Platform, Share } from 'react-native';

export type MilestoneShareResult = 'shared' | 'copied' | 'unavailable';

type ShareDeps = {
  nativeShare(message: string): Promise<void>;
  webShare?(text: string): Promise<void>;
  copyText?(text: string): Promise<void>;
};

function defaultDeps(): ShareDeps {
  const globals = globalThis as {
    navigator?: { share?: (data: { text: string }) => Promise<void>; clipboard?: { writeText(text: string): Promise<void> } };
  };
  return {
    nativeShare: async (message) => {
      await Share.share({ message });
    },
    webShare: globals.navigator?.share ? async (text) => globals.navigator?.share?.({ text }) : undefined,
    copyText: globals.navigator?.clipboard ? async (text) => globals.navigator?.clipboard?.writeText(text) : undefined,
  };
}

/** Shares the milestone line through the best channel on this platform. */
export async function shareMilestone(text: string, deps: ShareDeps = defaultDeps()): Promise<MilestoneShareResult> {
  if (Platform.OS !== 'web') {
    await deps.nativeShare(text);
    return 'shared';
  }
  if (deps.webShare) {
    await deps.webShare(text);
    return 'shared';
  }
  if (deps.copyText) {
    await deps.copyText(text);
    return 'copied';
  }
  return 'unavailable';
}
