// Measured Mall budgets (#357): draws, triangles, and anchors per shipped room
// file, read from the GLB JSON chunk. Run with `node scripts/mall-perf.mjs`;
// the memo quotes these numbers and rooms.glb.test.ts enforces the limits.
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');

function extractJson(buffer) {
  const view = new DataView(buffer.buffer, buffer.byteOffset, buffer.byteLength);
  if (view.getUint32(0, true) !== 0x46546c67) throw new Error('not a GLB');
  let offset = 12;
  while (offset + 8 <= buffer.byteLength) {
    const length = view.getUint32(offset, true);
    const kind = view.getUint32(offset + 4, true);
    if (kind === 0x4e4f534a) return JSON.parse(Buffer.from(buffer.buffer, buffer.byteOffset + offset + 8, length).toString('utf8'));
    offset += 8 + length;
  }
  throw new Error('no JSON chunk');
}

function stats(json) {
  let draws = 0;
  let tris = 0;
  for (const mesh of json.meshes ?? []) {
    for (const p of mesh.primitives ?? []) {
      if (p.mode !== undefined && p.mode !== 4) continue;
      draws += 1;
      const acc = p.indices !== undefined ? json.accessors[p.indices] : json.accessors[p.attributes?.POSITION];
      if (acc) tris += Math.floor(acc.count / 3);
    }
  }
  const anchors = (json.nodes ?? []).map((n) => n.name).filter((n) => typeof n === 'string' && n.startsWith('anchor_')).sort();
  return { draws, tris, anchors };
}

for (const room of ['video_store', 'toy_store', 'bookstore', 'mall_hub']) {
  const file = join(root, 'assets/stores', `${room}.glb`);
  const bytes = readFileSync(file);
  const { draws, tris, anchors } = stats(extractJson(bytes));
  console.log(`${room}: ${(bytes.length / 1024).toFixed(0)} KB, ${tris} tris, ${draws} draws, ${anchors.length} anchors`);
  console.log(`  ${anchors.join(' ')}`);
}
