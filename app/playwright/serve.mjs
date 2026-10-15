// Builds the mocked web export (unless E2E_SKIP_BUILD=1 and dist exists) and serves it on 127.0.0.1:<port>.
// The Expo "static" export has one HTML file per route, and dynamic routes live in "[param]" files, so this resolves those too.
import { spawnSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, normalize, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(fileURLToPath(new URL('..', import.meta.url)));
const dist = join(root, 'dist');
const port = Number(process.argv[2]);
if (!port || [8080, 8409, 32400, 8096, 5480].includes(port)) throw new Error(`Refusing port ${port}`);

if (process.env.E2E_SKIP_BUILD !== '1' || !existsSync(join(dist, 'index.html'))) {
  const build = spawnSync('npx expo export --platform web', {
    cwd: root,
    shell: true,
    stdio: ['ignore', 'ignore', 'inherit'],
    env: { ...process.env, EXPO_PUBLIC_API_MOCK: '1', EXPO_PUBLIC_E2E: '1', CI: '1' },
  });
  if (build.status !== 0) throw new Error('expo export failed');
}

const types = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.ttf': 'font/ttf',
  '.svg': 'image/svg+xml',
  '.map': 'application/json',
};

const isFile = (p) => existsSync(p) && statSync(p).isFile();

function resolveFile(urlPath) {
  const parts = decodeURIComponent(urlPath).split('/').filter(Boolean);
  const direct = normalize(join(dist, ...parts));
  if (!direct.startsWith(dist + sep) && direct !== dist) return null;
  if (isFile(direct)) return direct;
  for (const candidate of [`${direct}.html`, join(direct, 'index.html')]) if (isFile(candidate)) return candidate;
  // Dynamic routes: walk the segments, taking "[param]" entries where the literal one is missing.
  let dir = dist;
  for (let i = 0; i < parts.length; i++) {
    const last = i === parts.length - 1;
    const literal = join(dir, parts[i]);
    if (existsSync(literal) && statSync(literal).isDirectory() && !last) { dir = literal; continue; }
    if (last) {
      if (isFile(`${literal}.html`)) return `${literal}.html`;
      if (isFile(join(literal, 'index.html'))) return join(literal, 'index.html');
    }
    const dynamic = existsSyncDynamic(dir, last);
    if (!dynamic) return null;
    if (last && dynamic.file) return dynamic.file;
    if (!last && dynamic.dir) { dir = dynamic.dir; continue; }
    return null;
  }
  return null;
}

function existsSyncDynamic(dir, last) {
  const names = readdirSync(dir).filter((n) => n.startsWith('[') && !n.startsWith('[...'));
  for (const name of names) {
    const full = join(dir, name);
    if (last && isFile(full) && name.endsWith('.html')) return { file: full };
    if (statSync(full).isDirectory()) {
      if (last && isFile(join(full, 'index.html'))) return { file: join(full, 'index.html') };
      if (!last) return { dir: full };
    }
  }
  return null;
}

createServer((req, res) => {
  const { pathname } = new URL(req.url ?? '/', 'http://localhost');
  const file = resolveFile(pathname) ?? join(dist, '+not-found.html');
  res.writeHead(file.endsWith('+not-found.html') ? 404 : 200, { 'Content-Type': types[extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' });
  res.end(readFileSync(file));
}).listen(port, '127.0.0.1');
