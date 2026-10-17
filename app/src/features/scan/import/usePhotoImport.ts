// The Photos button's state for the scanner screen: starts an import, shows "Reading 3 of 8", cancels the rest.
import { useEffect, useMemo, useSyncExternalStore } from 'react';
import { IDLE, PhotoImport, type ImportDeps } from './photoImport';

const NO_SUBSCRIBE = () => () => undefined;
const NO_STATE = () => IDLE;

export function usePhotoImport(deps: ImportDeps | undefined) {
  const importer = useMemo(() => (deps ? new PhotoImport(deps) : undefined), [deps]);
  useEffect(() => () => importer?.cancel(), [importer]);
  const state = useSyncExternalStore(importer?.subscribe ?? NO_SUBSCRIBE, importer?.getState ?? NO_STATE, importer?.getState ?? NO_STATE);
  return {
    enabled: !!importer,
    running: state.running,
    progress: state.running && state.total > 0 ? `Reading ${state.index} of ${state.total}` : '',
    message: state.message,
    start: () => void importer?.start(),
    cancel: () => importer?.cancel(),
  };
}
