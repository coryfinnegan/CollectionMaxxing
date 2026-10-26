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
