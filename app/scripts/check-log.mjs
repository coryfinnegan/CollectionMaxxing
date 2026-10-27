// Where the quiet gate keeps its evidence (#260): the pinned dir when
// CM_APP_CHECK_LOG_DIR is set (CI points it somewhere uploadable), else a fresh
// tmp dir as before. check.mjs and the root gate agree on this one name.
import { mkdirSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

export const LOG_DIR_ENV = 'CM_APP_CHECK_LOG_DIR';

/** The gate's log dir, created. Takes the env to read so tests stay hermetic. */
export function resolveLogDir(env = process.env) {
  const pinned = env[LOG_DIR_ENV];
  if (pinned) {
    mkdirSync(pinned, { recursive: true });
    return pinned;
  }
  return mkdtempSync(join(tmpdir(), 'cm-app-check-'));
}
