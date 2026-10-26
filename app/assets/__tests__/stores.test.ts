// Mall-mode store rooms (#381): the Blender-authored GLB rooms and their camera
// presets. Budgets come from the issue: 150k triangles, 40 static draw calls,
// 1K-or-smaller textures, and anchor_<section>_<n> empties for item covers.
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { mallRooms, roomAnchorSections } from '@/features/mall/rooms';

const stores = join(__dirname, '..', 'stores');

const rooms = mallRooms;
type Room = (typeof rooms)[number];

/** The anchor sections each room must furnish, one cover row per section. */
const expectedSections: Record<Room, string[]> = roomAnchorSections;

const maxTriangles = 150_000;
const maxDrawCalls = 40;
const maxTextureSize = 1024;

interface Glb {
  json: any;
}

function readGlb(room: Room): Glb {
  const file = join(stores, `${room}.glb`);
  if (!existsSync(file)) throw new Error(`missing shipped asset: stores/${room}.glb`);
  const bytes = readFileSync(file);
  expect(bytes.subarray(0, 4).toString('ascii')).toBe('glTF');
  expect(bytes.readUInt32LE(4)).toBe(2);
  const jsonLength = bytes.readUInt32LE(12);
  expect(bytes.readUInt32LE(16)).toBe(0x4e4f534a);
  return { json: JSON.parse(bytes.subarray(20, 20 + jsonLength).toString('utf8')) };
}

/** Every mesh primitive is one static draw call; indexed triangles sum to the budget. */
function primitiveStats(gltf: any): { triangles: number; drawCalls: number } {
  let triangles = 0;
  let drawCalls = 0;
  for (const mesh of gltf.meshes ?? []) {
    for (const primitive of mesh.primitives) {
      drawCalls += 1;
      const mode = primitive.mode ?? 4;
      expect(mode).toBe(4);
      const accessor =
        primitive.indices !== undefined
          ? gltf.accessors[primitive.indices]
          : gltf.accessors[primitive.attributes.POSITION];
      triangles += Math.floor(accessor.count / 3);
    }
  }
  return { triangles, drawCalls };
}

function anchorNames(gltf: any): string[] {
  return (gltf.nodes ?? []).map((n: any) => n.name ?? '').filter((n: string) => n.startsWith('anchor_'));
}

describe.each(rooms)('the %s room', (room) => {
  it('exports a valid GLB with flat materials and no baked textures', () => {
    const { json } = readGlb(room);
    expect(json.asset.version).toBe('2.0');
    expect(json.extensionsRequired ?? []).toEqual([]);
    // Flat Principled materials only: nothing to sample, nothing over 1K.
    for (const image of json.images ?? []) {
      throw new Error(`unexpected baked texture: ${image.name ?? '(unnamed)'}`);
    }
    for (const texture of json.textures ?? []) {
      throw new Error(`unexpected texture sampler: ${texture.source}`);
    }
    expect(maxTextureSize).toBe(1024);
  });

  it('stays inside the triangle and draw-call budgets', () => {
    const { json } = readGlb(room);
    const { triangles, drawCalls } = primitiveStats(json);
    expect(triangles).toBeGreaterThan(0);
    expect(triangles).toBeLessThanOrEqual(maxTriangles);
    expect(drawCalls).toBeLessThanOrEqual(maxDrawCalls);
  });

  it('names every cover anchor anchor_<section>_<n>', () => {
    const { json } = readGlb(room);
    const anchors = anchorNames(json);
    expect(anchors.length).toBeGreaterThan(0);
    expect(new Set(anchors).size).toBe(anchors.length);
    for (const name of anchors) {
      expect(name).toMatch(/^anchor_[a-z]+_\d+$/);
    }
  });

  it('furnishes every section the Mall scene graph expects', () => {
    const { json } = readGlb(room);
    const sections = new Set(anchorNames(json).map((n) => n.split('_')[1]));
    for (const section of expectedSections[room]) {
      expect(sections.has(section)).toBe(true);
    }
  });
});

describe('the store camera presets', () => {
  it('covers every room with a hero angle the spike can reuse', () => {
    const file = join(stores, 'presets.json');
    if (!existsSync(file)) throw new Error('missing shipped asset: stores/presets.json');
    const presets = JSON.parse(readFileSync(file, 'utf8'));
    for (const room of rooms) {
      expect(Array.isArray(presets[room])).toBe(true);
      expect(presets[room].length).toBeGreaterThanOrEqual(1);
      for (const preset of presets[room]) {
        expect(typeof preset.name).toBe('string');
        for (const key of ['location', 'target'] as const) {
          expect(Array.isArray(preset[key])).toBe(true);
          expect(preset[key]).toHaveLength(3);
          for (const v of preset[key]) expect(typeof v).toBe('number');
        }
        expect(typeof preset.lens).toBe('number');
      }
    }
  });
});
