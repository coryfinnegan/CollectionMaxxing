// CollectionMaxxing load check (QA-2a, #170): virtual scanners against a throwaway stack.
//
// Started by scripts/perf/Run-Perf.ps1 inside a k6 container on an isolated Docker network; it is never pointed at anything
// else (the runner has no URL parameter). Environment: PROFILE, BASE_URL, SETUP_CODE, RP_ID, ORIGIN.
//
// What one scanner does (docs/04 "Scanning", kept inside the docs/08 rate limits; the arithmetic is proved in tests.ps1):
//   - opens a scan session; every 2 to 4 s sends a barcode batch (1 to 5 codes) or an identify of 1 to 3 items;
//   - follows results by polling `GET /scan-sessions/{id}?since=` (k6 cannot hold an SSE stream; polling is the app's own fallback);
//   - an ownership delta sync every 20 s, a commit about once a minute (then a new session);
//   - library browsing and catalogue search at human speed; refreshes its token before it expires.
// A 429 is expected traffic here, not a failure: it is counted separately (rate_limited) and reported as a finding.
import http from 'k6/http';
import { sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';
import { SoftwareAuthenticator } from './webauthn.js';

const config = JSON.parse(open('../profiles.json'));
const PROFILE = __ENV.PROFILE || 'smoke';
const profile = config.profiles[PROFILE];
if (!profile) {
  throw new Error(`unknown profile "${PROFILE}"`);
}
const model = config.model;
const BASE_URL = __ENV.BASE_URL;
const RP_ID = __ENV.RP_ID;
const ORIGIN = __ENV.ORIGIN;

// ---- options: thresholds and abort rules (static-checked in tests.ps1) ------------------------------------------------
const run = 'http_req_duration{phase:run}';
const failed = 'http_req_failed{phase:run}';
export const options = {
  setupTimeout: '240s',
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  scenarios: {
    scanners: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: `${profile.rampSeconds}s`, target: profile.scanners },
        { duration: `${profile.durationSeconds - profile.rampSeconds}s`, target: profile.scanners },
      ],
      gracefulRampDown: '5s',
      gracefulStop: '10s',
    },
  },
  thresholds: {
    [run]: [
      `p(95)<${config.thresholds.p95Ms}`,
      { threshold: `p(95)<${config.abort.p95Ms}`, abortOnFail: true, delayAbortEval: `${config.abort.forSeconds}s` },
    ],
    [failed]: [
      `rate<${config.thresholds.errorRate}`,
      { threshold: `rate<${config.abort.errorRate}`, abortOnFail: true, delayAbortEval: `${config.abort.forSeconds}s` },
    ],
    'http_reqs{phase:run}': ['count>=0'],
  },
};

// 429 is an answer we expect to be able to get; everything else outside 2xx and 3xx is an error.
http.setResponseCallback(http.expectedStatuses({ min: 200, max: 399 }, 429));

const CALLS = ['session_open', 'barcodes', 'identify', 'poll', 'accept', 'sync', 'commit', 'browse', 'search', 'token_refresh'];
const trends = {};
const limited = {};
for (const name of CALLS) {
  trends[name] = new Trend(`call_${name}`, true);
  limited[name] = new Counter(`limited_${name}`);
}
const rateLimited = new Counter('rate_limited');
const candidatesSeen = new Counter('candidates_seen');
const itemsCommitted = new Counter('items_committed');

// ---- seed: users, devices and tokens through the API's own flows --------------------------------------------------------
class Browser {
  constructor() {
    this.cookies = {};
  }

  headers(extra) {
    const names = Object.keys(this.cookies);
    const h = { 'Content-Type': 'application/json', 'X-CSRF': '1', ...extra };
    if (names.length > 0) h.Cookie = names.map((n) => `${n}=${this.cookies[n]}`).join('; ');
    return h;
  }

  remember(res) {
    for (const name of Object.keys(res.cookies || {})) {
      const c = res.cookies[name][0];
      if (c && c.value) this.cookies[name] = c.value;
    }
  }

  send(method, path, body, what) {
    const res = http.request(method, `${BASE_URL}${path}`, body === undefined ? null : JSON.stringify(body), {
      headers: this.headers(),
      jar: new http.CookieJar(),
      tags: { phase: 'seed', name: `seed ${what}` },
      redirects: 0,
    });
    this.remember(res);
    return res;
  }
}

function must(res, status, what) {
  if (res.status !== status) {
    throw new Error(`seed: ${what} answered ${res.status} (wanted ${status}): ${String(res.body).slice(0, 200)}`);
  }
  return res;
}

async function register(browser, optionsResponse, inviteToken, what) {
  must(optionsResponse, 200, `${what} options`);
  const authenticator = await SoftwareAuthenticator.create(RP_ID, ORIGIN);
  const credential = await authenticator.createCredential(optionsResponse.json('options'));
  const query = inviteToken ? `?invite=${encodeURIComponent(inviteToken)}` : '';
  return must(browser.send('POST', `/account/passkey/register${query}`, { credential, name: 'Load passkey' }, `${what} register`), 201, `${what} register`).json();
}

export async function setup() {
  const owner = new Browser();
  const first = await register(owner, owner.send('POST', '/setup', { code: __ENV.SETUP_CODE, displayName: 'Load owner' }, 'setup'), null, 'owner');
  const libraryId = first.libraryId;
  const accounts = [owner];
  for (let i = 1; i < profile.accounts; i++) {
    const invite = must(
      owner.send('POST', `/api/v1/libraries/${libraryId}/invites`, { name: `Load member ${i}`, role: 'editor' }, 'invite'), 201, 'invite').json();
    const token = invite.link.substring(invite.link.lastIndexOf('/') + 1);
    const member = new Browser();
    const options = member.send('POST', '/account/passkey/creation-options', { inviteToken: token, displayName: `Load member ${i}` }, 'join options');
    await register(member, options, token, `member ${i}`);
    accounts.push(member);
  }

  const devices = [];
  for (let d = 0; d < profile.scanners; d++) {
    const account = accounts[d % accounts.length];
    const offer = must(account.send('POST', '/api/v1/me/devices/pair', undefined, 'pair'), 201, 'pair').json();
    const paired = must(
      http.post(`${BASE_URL}/connect/token`, { grant_type: 'urn:cm:device_pairing', pairing_token: offer.token, device_name: `Load scanner ${d}` },
        { tags: { phase: 'seed', name: 'seed redeem' } }), 200, 'redeem').json();
    devices.push({
      accessToken: paired.access_token,
      refreshToken: paired.refresh_token,
      expiresAt: Date.now() + paired.expires_in * 1000,
      libraryId,
      account: d % accounts.length,
    });
  }
  console.log(`seeded ${accounts.length} accounts and ${devices.length} devices`);
  return { devices };
}

// ---- the scanner -------------------------------------------------------------------------------------------------------
const rand = (min, max) => min + Math.random() * (max - min);
const randInt = (min, max) => Math.floor(rand(min, max + 1));
const pick = (list) => list[Math.floor(Math.random() * list.length)];

function ean13(n) {
  let digits = String(978000000000 + ((n * 7919 + 104729) % 1000000000)).padStart(12, '0');
  let sum = 0;
  for (let i = 0; i < 12; i++) sum += Number(digits[i]) * (i % 2 === 0 ? 1 : 3);
  return digits + String((10 - (sum % 10)) % 10);
}
const CODES = Array.from({ length: 300 }, (_, n) => ean13(n));
const TITLES = ['Super Mario 64', 'Dune', 'Jurassic Park', 'The Legend of Zelda', 'Blade Runner', 'Neuromancer', 'Alien', 'Metroid', 'The Hobbit', 'Casablanca'];
const PLATFORMS = ['Nintendo 64', 'Super Nintendo', 'PlayStation', 'Sega Genesis'];
const WORDS = ['mario', 'zelda', 'dune', 'alien', 'hobbit', 'matrix', 'sonic', 'tetris', 'jaws', 'tron'];

let state = null;

function newState(device) {
  const now = Date.now();
  return {
    device,
    token: device.accessToken,
    refresh: device.refreshToken,
    expiresAt: device.expiresAt,
    sessionId: null,
    seq: 0,
    cursor: null,
    pending: [],
    next: {
      scan: now + rand(0, 2000),
      poll: now + rand(500, 3000),
      sync: now + rand(0, config.model.syncEverySeconds * 1000),
      commit: now + rand(30000, model.commitEverySeconds * 1000),
      browse: now + rand(0, model.browseEverySeconds * 1000),
      search: now + rand(0, model.searchEverySeconds * 1000),
    },
  };
}

/** One API call: tags it, times it per call type, and counts a 429 instead of timing it. */
function call(st, name, method, path, body, extra) {
  const params = {
    headers: { Authorization: `Bearer ${st.token}`, 'Content-Type': 'application/json', ...(extra && extra.headers) },
    tags: { phase: 'run', name },
  };
  if (extra && extra.responseCallback) params.responseCallback = extra.responseCallback;
  const res = http.request(method, `${BASE_URL}${path}`, body === undefined ? null : JSON.stringify(body), params);
  if (res.status === 429) {
    rateLimited.add(1);
    limited[name].add(1);
  } else {
    trends[name].add(res.timings.duration);
  }
  return res;
}

function refreshToken(st) {
  const res = http.post(`${BASE_URL}/connect/token`, { grant_type: 'refresh_token', refresh_token: st.refresh },
    { tags: { phase: 'run', name: 'token_refresh' } });
  if (res.status === 200) {
    const body = res.json();
    st.token = body.access_token;
    st.refresh = body.refresh_token;
    st.expiresAt = Date.now() + body.expires_in * 1000;
    trends.token_refresh.add(res.timings.duration);
  } else if (res.status === 429) {
    rateLimited.add(1);
    limited.token_refresh.add(1);
  }
}

function openSession(st) {
  const res = call(st, 'session_open', 'POST', `/api/v1/libraries/${st.device.libraryId}/scan-sessions`, {});
  if (res.status === 201) {
    st.sessionId = res.json('id');
    st.seq = 0;
    st.pending = [];
  }
}

function scan(st) {
  const base = `/api/v1/scan-sessions/${st.sessionId}`;
  if (Math.random() < model.barcodeShare) {
    const codes = Array.from({ length: randInt(model.codesPerBatch[0], model.codesPerBatch[1]) }, () => ({
      value: pick(CODES), symbology: 'ean_13', copies: 1,
    }));
    call(st, 'barcodes', 'POST', `${base}/barcodes`, { codes }, { headers: { 'Idempotency-Key': crypto.randomUUID() } });
  } else {
    const items = Array.from({ length: randInt(model.itemsPerIdentify[0], model.itemsPerIdentify[1]) }, (_, i) => {
      const title = pick(TITLES);
      return { localId: `snap-${i}`, text: title, title, kind: 'game', platform: pick(PLATFORMS) };
    });
    call(st, 'identify', 'POST', `${base}/identify`, { snapId: crypto.randomUUID(), trigger: 'auto', engine: 'apple_model', items });
  }
}

function poll(st) {
  const res = call(st, 'poll', 'GET', `/api/v1/scan-sessions/${st.sessionId}?since=${st.seq}`);
  if (res.status !== 200) return;
  const body = res.json();
  for (const c of body.candidates || []) {
    candidatesSeen.add(1);
    if (typeof c.seq === 'number' && c.seq > st.seq) st.seq = c.seq;
    if (c.resolution === 'matched' && st.pending.length < model.acceptsPerCommit) st.pending.push(c.id);
  }
}

function sync(st) {
  const query = st.cursor ? `?since=${encodeURIComponent(st.cursor)}&limit=200` : '?limit=200';
  const res = call(st, 'sync', 'GET', `/api/v1/libraries/${st.device.libraryId}/ownership/snapshot${query}`);
  if (res.status === 200) {
    const next = res.json('nextCursor');
    if (next) st.cursor = next;
  }
}

function commit(st) {
  const base = `/api/v1/scan-sessions/${st.sessionId}`;
  for (const id of st.pending) {
    call(st, 'accept', 'PATCH', `${base}/candidates/${id}`, { decision: 'accept' }, { responseCallback: http.expectedStatuses({ min: 200, max: 399 }, 409, 429) });
  }
  const res = call(st, 'commit', 'POST', `${base}/commit`, {}, { headers: { 'Idempotency-Key': crypto.randomUUID() } });
  if (res.status === 200) itemsCommitted.add(Number(res.json('created')) || 0);
  st.sessionId = null; // the next tick opens a fresh session
}

export default function (data) {
  if (state === null) state = newState(data.devices[(__VU - 1) % data.devices.length]);
  const st = state;
  const now = Date.now();

  if (st.expiresAt - now < 90000) refreshToken(st);
  if (st.sessionId === null) openSession(st);

  if (st.sessionId !== null) {
    if (now >= st.next.scan) {
      scan(st);
      st.next.scan = now + rand(model.scanIntervalSeconds[0] * 1000, model.scanIntervalSeconds[1] * 1000);
    }
    if (now >= st.next.poll) {
      poll(st);
      st.next.poll = now + model.pollEverySeconds * 1000;
    }
    if (now >= st.next.commit) {
      commit(st);
      st.next.commit = now + model.commitEverySeconds * 1000;
    }
  }
  if (now >= st.next.sync) {
    sync(st);
    st.next.sync = now + model.syncEverySeconds * 1000;
  }
  if (now >= st.next.browse) {
    call(st, 'browse', 'GET', `/api/v1/libraries/${st.device.libraryId}/items?limit=50&sort=title`);
    st.next.browse = now + model.browseEverySeconds * 1000;
  }
  if (now >= st.next.search) {
    call(st, 'search', 'GET', `/api/v1/catalog/search?q=${pick(WORDS)}`);
    st.next.search = now + model.searchEverySeconds * 1000;
  }

  const soonest = Math.min(...Object.values(st.next));
  sleep(Math.min(1, Math.max(0.1, (soonest - Date.now()) / 1000)));
}

// ---- the summary: a short text for the console and the numbers as JSON for the runner ------------------------------------
function trend(data, name) {
  const m = data.metrics[`call_${name}`];
  if (!m || !m.values.count) return null;
  const v = m.values;
  return { count: v.count, p50: round(v.med), p95: round(v['p(95)']), p99: round(v['p(99)']), max: round(v.max) };
}

const round = (n) => Math.round(n * 10) / 10;

export function handleSummary(data) {
  const calls = {};
  const limitedBy = {};
  for (const name of CALLS) {
    const t = trend(data, name);
    if (t) calls[name] = t;
    const l = data.metrics[`limited_${name}`];
    if (l && l.values.count) limitedBy[name] = l.values.count;
  }
  const count = (name) => (data.metrics[name] ? data.metrics[name].values.count || 0 : 0);
  const duration = data.metrics[run] ? data.metrics[run].values : {};
  const failedRate = data.metrics[failed] ? data.metrics[failed].values.rate : 0;
  const results = [];
  let aborted = false;
  let passed = true;
  for (const metric of [run, failed]) {
    const thresholds = (data.metrics[metric] && data.metrics[metric].thresholds) || {};
    for (const expression of Object.keys(thresholds)) {
      results.push({ metric, expression, ok: thresholds[expression].ok });
      if (!thresholds[expression].ok) {
        passed = false;
        if (expression.includes(String(config.abort.p95Ms)) || expression.includes(String(config.abort.errorRate))) aborted = true;
      }
    }
  }
  const summary = {
    profile: PROFILE,
    scanners: profile.scanners,
    accounts: profile.accounts,
    plannedSeconds: profile.durationSeconds,
    ranSeconds: round((data.state.testRunDurationMs || 0) / 1000),
    requests: count('http_reqs{phase:run}'),
    errorRate: failedRate,
    rateLimited: count('rate_limited'),
    rateLimitedBy: limitedBy,
    p50: round(duration.med || 0),
    p95: round(duration['p(95)'] || 0),
    p99: round(duration['p(99)'] || 0),
    calls,
    candidatesSeen: count('candidates_seen'),
    itemsCommitted: count('items_committed'),
    thresholds: results,
    thresholdsPassed: passed,
    aborted,
  };
  const lines = [
    `k6 ${PROFILE}: ${summary.requests} requests, ${summary.scanners} scanners / ${summary.accounts} accounts, ran ${summary.ranSeconds}s of ${summary.plannedSeconds}s`,
    `all calls: p50 ${summary.p50} ms, p95 ${summary.p95} ms, p99 ${summary.p99} ms, errors ${(failedRate * 100).toFixed(2)}%, 429s ${summary.rateLimited}`,
    ...Object.keys(calls).map((n) => `  ${n.padEnd(14)} n=${String(calls[n].count).padEnd(6)} p50 ${calls[n].p50}  p95 ${calls[n].p95}  p99 ${calls[n].p99}`),
    `thresholds: ${passed ? 'passed' : 'FAILED'}${aborted ? ' (run aborted by an abort rule)' : ''}`,
  ];
  return { '/out/k6-summary.json': JSON.stringify(summary, null, 2), stdout: `${lines.join('\n')}\n` };
}
