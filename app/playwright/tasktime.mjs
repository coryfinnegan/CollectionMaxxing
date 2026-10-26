// Task-time measurement for the Castle Phase 1 acceptance (#363): themed (Full)
// vs Flat (Quiet) median task time on browse, find and edit tasks. (Scan is
// 0% by construction: the PR touches no scan file and adds no tap. See the
// PR comment for that evidence.)
//
// What it measures: wall-clock time for three scripted user tasks on the web
// grid view (a themed surface: battlement header, shelves, state dragons) with
// the mock API, under Theme Full vs Quiet. Dragon appearances stay at the
// default (Full) in both conditions, so the delta isolates the castle frame.
// Playwright action overhead applies equally to both conditions, so the
// comparison holds even though absolute times include harness overhead.
//
// Method: fresh browser context per run (cold cache, seeded theme), conditions
// interleaved with alternating pair order to cancel machine drift. Each run
// asserts its own condition (Full renders `castle-texture` art, Quiet renders
// none) and fails on any page error. Reports per-task medians and the % slower
// of Full vs Quiet.
//
// Run from app/: `node playwright/tasktime.mjs [runs-per-condition]`
// (default 25). Always builds the export for its own random port, because the
// API URL is baked in at build time and a reused dist would trip the
// connect-src CSP on a different port.
// This is a manual measurement, not a gate: it is not *.spec.ts, so CI and
// the app check never run it.
import { spawn } from 'node:child_process';
import { setTimeout as sleep } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { chromium, expect } from '@playwright/test';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const RUNS = Number(process.argv[2] ?? 25);
const TIMEOUT = 10_000;

const port = 41000 + Math.floor(Math.random() * 9000);
const baseURL = `http://127.0.0.1:${port}`;

function median(values) {
  const sorted = [...values].sort((a, b) => a - b);
  const mid = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
}

async function waitForServer(server) {
  for (let i = 0; i < 240; i++) {
    if (server.exitCode !== null) throw new Error(`web server exited with code ${server.exitCode}`);
    try {
      const res = await fetch(`${baseURL}/login`);
      if (res.ok) return;
    } catch {
      // Not up yet.
    }
    await sleep(500);
  }
  throw new Error('web server never came up');
}

// One full page of tasks under one theme. Returns per-task ms plus the texture count.
async function measureRun(browser, theme) {
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  try {
    // MMKV-on-web persists under `<id>\<key>` in localStorage; seeding before
    // the first load puts the kept Theme setting in place for RootFrame.
    await context.addInitScript((seed) => {
      localStorage.setItem('castle-settings\\castle-theme', seed);
    }, theme);
    const page = await context.newPage();
    const errors = [];
    const debug = process.env.TASKTIME_DEBUG === '1';
    page.on('console', (m) => {
      if (debug) console.log(`[console:${m.type()}] ${m.text().slice(0, 200)}`);
      if (m.type() === 'error') errors.push(m.text());
    });
    page.on('pageerror', (e) => errors.push(e.message));
    if (debug) {
      page.on('requestfailed', (r) => console.log(`[requestfailed] ${r.url()} ${r.failure()?.errorText}`));
      page.on('response', (r) => {
        if (r.url().includes('/api/')) console.log(`[api] ${r.status()} ${r.url().slice(0, 140)}`);
      });
    }

    await page.goto(`${baseURL}/library`);
    await page.waitForFunction(() => '__cm' in window, undefined, { timeout: TIMEOUT });
    await page.getByRole('button', { name: 'Grid view' }).click();
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toBeVisible({ timeout: TIMEOUT });

    // The run proves its own condition: Full paints texture art, Quiet paints none.
    const textures = await page.getByTestId('castle-texture').count();
    if (theme === 'full' && textures === 0) throw new Error('Full run rendered no castle textures');
    if (theme === 'quiet' && textures !== 0) throw new Error(`Quiet run rendered ${textures} castle textures`);

    // Browse: narrow to games, then back to everything.
    let start = Date.now();
    await page.getByRole('button', { name: 'Games', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Super Mario 64, Cartridge, Nintendo 64' })).toBeVisible({ timeout: TIMEOUT });
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toHaveCount(0, { timeout: TIMEOUT });
    await page.getByRole('button', { name: 'All', exact: true }).click();
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toBeVisible({ timeout: TIMEOUT });
    const browse = Date.now() - start;

    // Find: search for one title, then clear.
    start = Date.now();
    await page.getByRole('textbox', { name: 'Search' }).fill('dune');
    await expect(page.getByRole('button', { name: /Dune/ })).toBeVisible({ timeout: TIMEOUT });
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toHaveCount(0, { timeout: TIMEOUT });
    await page.getByRole('textbox', { name: 'Search' }).fill('');
    await expect(page.getByRole('button', { name: 'The Matrix, DVD' })).toBeVisible({ timeout: TIMEOUT });
    const find = Date.now() - start;

    // Edit: open an item, change its notes, save.
    start = Date.now();
    await page.getByRole('button', { name: 'The Matrix, DVD' }).click();
    await expect(page.getByRole('heading', { name: 'The Matrix' })).toBeVisible({ timeout: TIMEOUT });
    await page.getByRole('button', { name: 'Edit', exact: true }).click();
    await page.getByRole('textbox', { name: 'Notes' }).fill('Task time note');
    await page.getByRole('button', { name: 'Save changes' }).click();
    await expect(page.getByText('Task time note').first()).toBeVisible({ timeout: TIMEOUT });
    const edit = Date.now() - start;

    if (errors.length > 0) throw new Error(`page errors during ${theme} run: ${errors.join(' | ')}`);
    return { browse, find, edit, textures };
  } finally {
    await context.close();
  }
}

const server = spawn('node', [join(root, 'playwright/serve.mjs'), String(port)], {
  cwd: root,
  stdio: ['ignore', 'ignore', 'inherit'],
  // Never reuse a dist built for another port: the baked API URL would trip CSP.
  env: { ...process.env, E2E_SKIP_BUILD: '0' },
});
try {
  await waitForServer(server);
  const browser = await chromium.launch();
  try {
    const samples = { full: { browse: [], find: [], edit: [] }, quiet: { browse: [], find: [], edit: [] } };
    for (let i = 0; i < RUNS; i++) {
      // Alternate the pair order so neither condition always runs first.
      const pair = i % 2 === 0 ? ['full', 'quiet'] : ['quiet', 'full'];
      for (const theme of pair) {
        const times = await measureRun(browser, theme);
        samples[theme].browse.push(times.browse);
        samples[theme].find.push(times.find);
        samples[theme].edit.push(times.edit);
        console.log(JSON.stringify({ run: i, theme, ...times }));
      }
    }
    console.log('---');
    console.log(`runs per condition: ${RUNS} (fresh context each, interleaved, headless chromium 1280x800, mock API)`);
    for (const task of ['browse', 'find', 'edit']) {
      const full = median(samples.full[task]);
      const quiet = median(samples.quiet[task]);
      const slower = ((full - quiet) / quiet) * 100;
      console.log(
        `${task}: full median ${full.toFixed(0)}ms, quiet median ${quiet.toFixed(0)}ms -> full ${slower.toFixed(1)}% slower ` +
          `(full min/max ${Math.min(...samples.full[task])}/${Math.max(...samples.full[task])}ms, ` +
          `quiet min/max ${Math.min(...samples.quiet[task])}/${Math.max(...samples.quiet[task])}ms)`,
      );
    }
  } finally {
    await browser.close();
  }
} finally {
  server.kill();
}
