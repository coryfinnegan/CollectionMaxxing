// CollectionMaxxing load check (QA-2a, #170): virtual scanners against a throwaway stack.
//
// Started by scripts/perf/Run-Perf.ps1 inside a k6 container on an isolated Docker network; it is never pointed at anything
// else (the runner has no URL parameter). Environment: PROFILE, BASE_URL. The accounts' bearer tokens come from /out/tokens.json, which the
// runner signs with a fake Auth0's throwaway key (scripts/fake-auth0) before k6 starts.
//
// What one scanner does (docs/04 "Scanning", kept inside the docs/08 rate limits; the arithmetic is proved in tests.ps1):
//   - opens a scan session; every 2 to 4 s sends a barcode batch (1 to 5 codes) or an identify of 1 to 3 items;
//   - follows results by polling `GET /scan-sessions/{id}?since=` (k6 cannot hold an SSE stream; polling is the app's own fallback);
//   - an ownership delta sync every 20 s, a commit about once a minute (then a new session);
//   - library browsing and catalogue search at human speed. Its access token lasts an hour, longer than any profile runs.
// A 429 is expected traffic here, not a failure: it is counted separately (rate_limited) and reported as a finding.
import http from 'k6/http';
import encoding from 'k6/encoding';
import { sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';

const config = JSON.parse(open('../profiles.json'));
const PROFILE = __ENV.PROFILE || 'smoke';
const profile = config.profiles[PROFILE];
if (!profile) {
  throw new Error(`unknown profile "${PROFILE}"`);
}
const model = config.model;
const BASE_URL = __ENV.BASE_URL;
// One token per account, signed by the runner for the fake Auth0 the API trusts (the file is in the run's temp folder, mounted at /out).
const TOKEN_FILE = JSON.parse(open('/out/tokens.json'));
const TOKENS = TOKEN_FILE.tokens;

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

const CALLS = ['session_open', 'barcodes', 'identify', 'poll', 'accept', 'sync', 'commit', 'browse', 'search'];
const trends = {};
const limited = {};
const counts = {};
for (const name of CALLS) {
  trends[name] = new Trend(`call_${name}`, true);
  limited[name] = new Counter(`limited_${name}`);
  counts[name] = new Counter(`count_${name}`);
}
const rateLimited = new Counter('rate_limited');
const candidatesSeen = new Counter('candidates_seen');
const itemsCommitted = new Counter('items_committed');

// ---- seed: accounts, a library, an invite and its acceptance, through the API's own flows ------------------------------------
class Account {
  /** Calls as a bearer token (the iPhone), or, with `cookies`, as a web session (the owner's sign-in, which an invite needs). */
  constructor(token, cookies) {
    this.token = token;
    this.cookies = cookies || null;
  }

  send(method, path, body, what) {
    const headers = { 'Content-Type': 'application/json', 'X-CSRF': '1' };
    if (this.cookies) headers.Cookie = cookieHeader(this.cookies);
    else headers.Authorization = `Bearer ${this.token}`;
    return http.request(method, `${BASE_URL}${path}`, body === undefined ? null : JSON.stringify(body), {
      headers,
      jar: new http.CookieJar(),
      tags: { phase: 'seed', name: `seed ${what}` },
      redirects: 0,
    });
  }
}

const cookieHeader = (cookies) => Object.keys(cookies).map((n) => `${n}=${cookies[n]}`).join('; ');

function remember(cookies, res) {
  for (const name of Object.keys(res.cookies || {})) {
    const c = res.cookies[name][0];
    if (c && c.value) cookies[name] = c.value;
    else delete cookies[name];
  }
}

function queryOf(location) {
  const out = {};
  for (const pair of String(location).split('?')[1].split('&')) {
    const [k, v] = pair.split('=');
    out[k] = decodeURIComponent((v || '').replace(/\+/g, ' '));
  }
  return out;
}

/**
 * The owner signs in on the web, playing the browser against the fake Auth0: the login redirects to Auth0's authorize address (with the state
 * and nonce), and the callback gets a code the fake turns into an ID token for the owner (the code is base64url of {sub, nonce}).
 * Returns the cookies of the session. A bearer token never counts as a recent sign-in, which creating an invite needs.
 */
function webSignIn(sub) {
  const cookies = {};
  const get = (path, what) => http.get(`${BASE_URL}${path}`, {
    headers: { Cookie: cookieHeader(cookies) }, jar: new http.CookieJar(), redirects: 0, tags: { phase: 'seed', name: `seed ${what}` },
  });
  const start = must(get('/account/login?returnUrl=%2F', 'web sign-in'), 302, 'web sign-in start');
  remember(cookies, start);
  const authorize = queryOf(start.headers.Location);
  const code = encoding.b64encode(JSON.stringify({ sub, nonce: authorize.nonce }), 'rawurl');
  const callback = get(`/signin-oidc?code=${encodeURIComponent(code)}&state=${encodeURIComponent(authorize.state)}`, 'web sign-in callback');
  remember(cookies, callback);
  if (callback.status !== 302 || callback.headers.Location !== '/') {
    throw new Error(`seed: the web sign-in callback answered ${callback.status} to ${callback.headers.Location}`);
  }
  return cookies;
}

function must(res, status, what) {
  if (res.status !== status) {
    throw new Error(`seed: ${what} answered ${res.status} (wanted ${status}): ${String(res.body).slice(0, 200)}`);
  }
  return res;
}

export function setup() {
  if (TOKENS.length < profile.accounts) {
    throw new Error(`seed: ${profile.accounts} accounts needed but tokens.json has ${TOKENS.length}`);
  }
  // The owner signs in on the web (that makes the account and gives a recent sign-in), then makes the library and one invite per other account.
  const web = new Account(null, webSignIn(TOKEN_FILE.ownerSub));
  const libraryId = must(web.send('POST', '/api/v1/libraries', { name: 'Load library' }, 'create library'), 201, 'create library').json('id');
  const accounts = [new Account(TOKENS[0])];
  for (let i = 1; i < profile.accounts; i++) {
    const invite = must(
      web.send('POST', `/api/v1/libraries/${libraryId}/invites`, { name: `Load member ${i}`, role: 'editor' }, 'invite'), 201, 'invite').json();
    // A member's first call with a token the API has not seen makes the account (it asks the fake Auth0 who the token is); then the code is accepted.
    const member = new Account(TOKENS[i]);
    must(member.send('GET', '/api/v1/me', undefined, `member ${i} first call`), 200, `member ${i} first call`);
    must(member.send('POST', '/api/v1/invites:accept', { invite: invite.code }, 'accept invite'), 200, `member ${i} accept invite`);
    accounts.push(member);
  }

  const devices = [];
  for (let d = 0; d < profile.scanners; d++) {
    devices.push({ accessToken: accounts[d % accounts.length].token, libraryId, account: d % accounts.length });
  }
  console.log(`seeded ${accounts.length} accounts for ${devices.length} scanners`);
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
    counts[name].add(1);
  }
  return res;
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
  const n = data.metrics[`count_${name}`];
  if (!m || !n || !n.values.count) return null;
  const v = m.values;
  return { count: n.values.count, p50: round(v.med), p95: round(v['p(95)']), p99: round(v['p(99)']), max: round(v.max) };
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
