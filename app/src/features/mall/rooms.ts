// Mall-mode store rooms (#381): the Blender-authored GLB rooms, their camera
// presets, and the asset-loading seam the Mall spike codes against. The scene
// loads the GLB when the bundler has it and falls back to procedural geometry
// when not; either way covers attach at the same anchor_<section>_<n> nodes,
// so tap-to-identify picking works through one anchor contract.
import presetsJson from '@/assets/stores/presets.json';

export const mallRooms = ['video_store', 'toy_store', 'bookstore', 'mall_hub'] as const;
export type MallRoomId = (typeof mallRooms)[number];

export interface CameraPreset {
  name: string;
  location: [number, number, number];
  target: [number, number, number];
  lens: number;
}

function isPreset(value: unknown): value is CameraPreset {
  if (typeof value !== 'object' || value === null) return false;
  const preset = value as Record<string, unknown>;
  const isTriple = (v: unknown): v is [number, number, number] =>
    Array.isArray(v) && v.length === 3 && v.every((n) => typeof n === 'number');
  return typeof preset.name === 'string' && isTriple(preset.location) && isTriple(preset.target) && typeof preset.lens === 'number';
}

function loadRoomPresets(): Record<MallRoomId, CameraPreset[]> {
  const json = presetsJson as Record<string, unknown>;
  const rooms = {} as Record<MallRoomId, CameraPreset[]>;
  for (const room of mallRooms) {
    const presets = json[room];
    if (!Array.isArray(presets) || presets.length === 0 || !presets.every(isPreset)) {
      throw new Error(`missing or malformed camera presets for Mall room: ${room}`);
    }
    rooms[room] = presets;
  }
  return rooms;
}

/** The Blender-authored hero angles per room; the spike reuses them for its orbit camera. */
export const roomPresets: Record<MallRoomId, CameraPreset[]> = loadRoomPresets();

/** The anchor sections each room furnishes, one cover row per section. */
export const roomAnchorSections: Record<MallRoomId, string[]> = {
  video_store: ['shelf', 'new', 'counter'],
  toy_store: ['wall', 'case', 'peg'],
  bookstore: ['stack', 'mezz', 'cafe', 'table'],
  mall_hub: ['store', 'kiosk'],
};

/**
 * Every anchor node each room furnishes, in the Blender script's placement
 * order. The GLB seam test pins these against the shipped room files, so the
 * scripts, the assets, and the scene graph cannot drift apart.
 */
export const roomAnchors: Record<MallRoomId, string[]> = {
  video_store: [
    'anchor_shelf_1',
    'anchor_shelf_2',
    'anchor_shelf_3',
    'anchor_shelf_4',
    'anchor_shelf_5',
    'anchor_shelf_6',
    'anchor_new_1',
    'anchor_new_2',
    'anchor_counter_1',
  ],
  toy_store: ['anchor_wall_1', 'anchor_wall_2', 'anchor_peg_1', 'anchor_peg_2', 'anchor_case_1'],
  bookstore: ['anchor_stack_1', 'anchor_stack_2', 'anchor_mezz_1', 'anchor_cafe_1', 'anchor_table_1'],
  mall_hub: ['anchor_store_1', 'anchor_store_2', 'anchor_store_3', 'anchor_kiosk_1', 'anchor_kiosk_2'],
};

export type RoomDims = { width: number; depth: number; height: number };

/**
 * The room footprints in meters, mirroring the Blender scripts' P dicts. The
 * procedural shells build to these so the GLB anchors land inside them.
 */
export const roomDims: Record<MallRoomId, RoomDims> = {
  video_store: { width: 14, depth: 10, height: 3.2 },
  toy_store: { width: 14, depth: 10, height: 3 },
  bookstore: { width: 16, depth: 11, height: 4.6 },
  mall_hub: { width: 18, depth: 14, height: 4.2 },
};

/** The GLB node name a cover row attaches to: anchor_<section>_<n>. */
export function anchorName(section: string, index: number): string {
  return `anchor_${section}_${index}`;
}

export interface ParsedAnchor {
  section: string;
  index: number;
}

const anchorPattern = /^anchor_([a-z]+)_(\d+)$/;

/**
 * Parses a tapped GLB node name back to its cover row. Returns null for
 * static geometry, so picking ignores everything that is not a cover anchor.
 */
export function parseAnchorName(nodeName: string): ParsedAnchor | null {
  const match = anchorPattern.exec(nodeName);
  if (!match) return null;
  return { section: match[1], index: Number(match[2]) };
}

export type StoreRoomSource = { kind: 'glb'; asset: number } | { kind: 'procedural' };

/**
 * Resolves how the scene builds a room: the shipped GLB asset when present,
 * the procedural placeholder geometry when not. The caller requires the GLB
 * module id (require('@/assets/stores/<room>.glb')) and passes null when the
 * bundler cannot serve it; both paths expose the same anchor nodes.
 */
export function resolveRoomSource(glbAsset: number | null | undefined): StoreRoomSource {
  if (glbAsset === null || glbAsset === undefined) return { kind: 'procedural' };
  return { kind: 'glb', asset: glbAsset };
}
