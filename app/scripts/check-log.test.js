// The quiet gate keeps its evidence where CI can upload it (#260): a pinned dir
// when CM_APP_CHECK_LOG_DIR is set, else a fresh tmp dir as before.
const { existsSync, mkdtempSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { LOG_DIR_ENV, resolveLogDir } = require('./check-log.mjs');

describe('check gate log dir', () => {
  it('uses the pinned dir when set, creating it', () => {
    const scratch = mkdtempSync(join(tmpdir(), 'cm-pin-top-'));
    const pinned = join(scratch, 'nested', 'logs');
    try {
      expect(resolveLogDir({ [LOG_DIR_ENV]: pinned })).toBe(pinned);
      expect(existsSync(pinned)).toBe(true);
    } finally {
      rmSync(scratch, { recursive: true, force: true });
    }
  });

  it('falls back to a fresh tmp dir when unset', () => {
    const dir = resolveLogDir({});
    try {
      expect(dir.startsWith(join(tmpdir(), 'cm-app-check-'))).toBe(true);
      expect(existsSync(dir)).toBe(true);
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});
