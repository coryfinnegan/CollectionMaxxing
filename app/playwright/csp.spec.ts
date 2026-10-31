import { createHash } from 'node:crypto';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test } from '@playwright/test';

// The web export (`expo export`, built into dist by serve.mjs) may hold inline scripts, and the Content-Security-Policy names each one by
// its hash: a browser refuses any other. If Expo starts inlining something else, or the policy loses a hash, this fails here instead of
// as a blank page. The policy is read from the live response; SecurityHeadersTests keeps it equal to the one the API sends. (This
// check used to be a .NET test that quietly passed when dist was missing, which is always the case in the backend jobs.)
const dist = join(process.cwd(), 'dist');

const pagesIn = (dir: string): string[] =>
  readdirSync(dir, { withFileTypes: true }).flatMap((entry) =>
    entry.isDirectory() ? pagesIn(join(dir, entry.name)) : entry.name.endsWith('.html') ? [join(dir, entry.name)] : [],
  );

test('every inline script in the real web export is allowed by the Content-Security-Policy', async ({ request }) => {
  const policy = (await request.get('/')).headers()['content-security-policy'];
  expect(policy, 'the e2e server sends the policy').toBeTruthy();
  const pages = pagesIn(dist);
  expect(pages.length, `the export has pages in ${dist}`).toBeGreaterThan(0);

  const inline = pages.flatMap((page) =>
    [...readFileSync(page, 'utf8').matchAll(/<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/gi)].map((match) => ({ page, script: match[1] ?? '' })),
  );
  for (const { page, script } of inline) {
    const hash = `'sha256-${createHash('sha256').update(script, 'utf8').digest('base64')}'`;
    expect(policy, `an inline script in ${page} is not covered by the Content-Security-Policy: ${script.slice(0, 80)}`).toContain(hash);
  }
});
