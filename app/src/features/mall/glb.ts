// The smallest possible GLB reader for the Mall scene (#357): it pulls the JSON
// chunk out of a room file and reads the anchor_<section>_<n> node transforms,
// so covers attach at the Blender-authored anchors without a full glTF loader.
// Positions come out in meters, Y-up, matching the three.js scene. Anything
// malformed yields null or an empty list — the scene falls back to its
// procedural anchors instead of failing.
import { parseAnchorName } from './rooms';

export type GlbAnchor = {
  name: string;
  section: string;
  index: number;
  /** Meters, Y-up: where the cover row stands. */
  position: [number, number, number];
  /** Radians about Y: the aisle direction the covers face. */
  yaw: number;
};

export type GlbStats = {
  /** Mesh primitives: one static draw call each. */
  draws: number;
  /** Triangles across every primitive. */
  tris: number;
  /** Anchor node names, sorted. */
  anchors: string[];
};

const GLB_MAGIC = 0x46546c67;
const JSON_CHUNK = 0x4e4f534a;

function textDecoder(): { decode(bytes: Uint8Array): string } {
  const globals = globalThis as { TextDecoder?: new () => { decode(bytes: Uint8Array): string } };
  if (globals.TextDecoder) return new globals.TextDecoder();
  return {
    decode: (input: Uint8Array) => {
      let text = '';
      for (const byte of input) text += String.fromCharCode(byte);
      return text;
    },
  };
}

/** The JSON document inside a .glb file, or null when the bytes are not a GLB. */
export function extractGlbJson(buffer: ArrayBuffer): unknown | null {
  if (buffer.byteLength < 20) return null;
  const view = new DataView(buffer);
  if (view.getUint32(0, true) !== GLB_MAGIC || view.getUint32(4, true) !== 2) return null;
  let offset = 12;
  while (offset + 8 <= buffer.byteLength) {
    const length = view.getUint32(offset, true);
    const kind = view.getUint32(offset + 4, true);
    const start = offset + 8;
    if (kind === JSON_CHUNK) {
      try {
        return JSON.parse(textDecoder().decode(new Uint8Array(buffer, start, length)));
      } catch {
        return null;
      }
    }
    offset = start + length;
  }
  return null;
}

type GltfNode = {
  name?: string;
  children?: number[];
  matrix?: number[];
  translation?: number[];
  rotation?: number[];
  scale?: number[];
};

type Gltf = {
  nodes?: GltfNode[];
  meshes?: { primitives: { indices?: number; attributes?: { POSITION?: number }; mode?: number }[] }[];
  accessors?: { count: number; componentType: number }[];
};

function isGltf(value: unknown): value is Gltf {
  return typeof value === 'object' && value !== null;
}

function localMatrix(node: GltfNode): number[] {
  if (Array.isArray(node.matrix) && node.matrix.length === 16 && node.matrix.every((n) => typeof n === 'number')) {
    return [...node.matrix];
  }
  const t = Array.isArray(node.translation) ? node.translation : [0, 0, 0];
  const r = Array.isArray(node.rotation) ? node.rotation : [0, 0, 0, 1];
  const s = Array.isArray(node.scale) ? node.scale : [1, 1, 1];
  const [x, y, z, w] = [r[0] ?? 0, r[1] ?? 0, r[2] ?? 0, r[3] ?? 1];
  const [sx, sy, sz] = [s[0] ?? 1, s[1] ?? 1, s[2] ?? 1];
  const [tx, ty, tz] = [t[0] ?? 0, t[1] ?? 0, t[2] ?? 0];
  const xx = x * x;
  const xy = x * y;
  const xz = x * z;
  const xw = x * w;
  const yy = y * y;
  const yz = y * z;
  const yw = y * w;
  const zz = z * z;
  const zw = z * w;
  return [
    (1 - 2 * (yy + zz)) * sx,
    2 * (xy + zw) * sx,
    2 * (xz - yw) * sx,
    0,
    2 * (xy - zw) * sy,
    (1 - 2 * (xx + zz)) * sy,
    2 * (yz + xw) * sy,
    0,
    2 * (xz + yw) * sz,
    2 * (yz - xw) * sz,
    (1 - 2 * (xx + yy)) * sz,
    0,
    tx,
    ty,
    tz,
    1,
  ];
}

/** Column-major multiply: out = a * b. */
function multiply(a: number[], b: number[]): number[] {
  const out = new Array<number>(16).fill(0);
  for (let col = 0; col < 4; col++) {
    for (let row = 0; row < 4; row++) {
      out[col * 4 + row] = a[row] * b[col * 4] + a[4 + row] * b[col * 4 + 1] + a[8 + row] * b[col * 4 + 2] + a[12 + row] * b[col * 4 + 3];
    }
  }
  return out;
}

/**
 * The anchor nodes with world transforms. Blender bakes the Z-up to Y-up turn
 * into the matrices on export, and the anchor's aisle side (Blender local +Y)
 * lands on glTF local -Z, so the yaw reads off the third column.
 */
export function parseGlbAnchors(gltf: unknown): GlbAnchor[] {
  if (!isGltf(gltf) || !Array.isArray(gltf.nodes)) return [];
  const nodes = gltf.nodes;
  const parent = new Map<number, number>();
  nodes.forEach((node, i) => {
    if (!node || !Array.isArray(node.children)) return;
    for (const child of node.children) {
      if (typeof child === 'number' && !parent.has(child)) parent.set(child, i);
    }
  });
  const world = (index: number): number[] => {
    const node = nodes[index];
    if (!node || typeof node !== 'object') return localMatrix({});
    const local = localMatrix(node);
    const up = parent.get(index);
    return up === undefined ? local : multiply(world(up), local);
  };
  const anchors: GlbAnchor[] = [];
  nodes.forEach((node, i) => {
    if (!node || typeof node.name !== 'string') return;
    const parsed = parseAnchorName(node.name);
    if (!parsed) return;
    const m = world(i);
    const forward: [number, number] = [-m[8], -m[10]];
    anchors.push({
      name: node.name,
      section: parsed.section,
      index: parsed.index,
      position: [m[12], m[13], m[14]],
      yaw: Math.atan2(forward[0], forward[1]),
    });
  });
  return anchors.sort((a, b) => (a.name < b.name ? -1 : 1));
}

/** Draw calls, triangles, and anchor names: the budget check for a room file. */
export function glbStats(gltf: unknown): GlbStats {
  const anchors = parseGlbAnchors(gltf).map((a) => a.name);
  if (!isGltf(gltf) || !Array.isArray(gltf.meshes) || !Array.isArray(gltf.accessors)) return { draws: 0, tris: 0, anchors };
  let draws = 0;
  let tris = 0;
  for (const mesh of gltf.meshes) {
    if (!mesh || !Array.isArray(mesh.primitives)) continue;
    for (const primitive of mesh.primitives) {
      if (!primitive || typeof primitive !== 'object') continue;
      // Mode 4 (triangles) is the default; the rooms author nothing else.
      if (primitive.mode !== undefined && primitive.mode !== 4) continue;
      draws += 1;
      const indexed = typeof primitive.indices === 'number' ? gltf.accessors[primitive.indices] : undefined;
      const positions =
        indexed ?? (typeof primitive.attributes?.POSITION === 'number' ? gltf.accessors[primitive.attributes.POSITION] : undefined);
      if (positions && typeof positions.count === 'number') tris += Math.floor(positions.count / 3);
    }
  }
  return { draws, tris, anchors };
}

/** Blender Z-up meters to three.js Y-up meters (for presets.json angles). */
export function blenderToThree(location: [number, number, number]): [number, number, number] {
  return [location[0], location[2], -location[1]];
}

/** A Blender lens in mm to a vertical field of view in degrees (35mm frame). */
export function lensToFov(lens: number): number {
  return (2 * Math.atan(12 / lens) * 180) / Math.PI;
}
