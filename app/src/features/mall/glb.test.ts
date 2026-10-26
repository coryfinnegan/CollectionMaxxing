// The smallest possible GLB reader (#357): anchors and budgets out of room files.
import { blenderToThree, extractGlbJson, glbStats, lensToFov, parseGlbAnchors } from './glb';

function glbFile(json: string): ArrayBuffer {
  const bytes = new TextEncoder().encode(json);
  const buffer = new ArrayBuffer(20 + bytes.length);
  const view = new DataView(buffer);
  view.setUint32(0, 0x46546c67, true);
  view.setUint32(4, 2, true);
  view.setUint32(8, buffer.byteLength, true);
  view.setUint32(12, bytes.length, true);
  view.setUint32(16, 0x4e4f534a, true);
  new Uint8Array(buffer, 20).set(bytes);
  return buffer;
}

describe('extractGlbJson', () => {
  it('reads the JSON chunk out of a GLB', () => {
    expect(extractGlbJson(glbFile('{"nodes":[]}'))).toEqual({ nodes: [] });
  });

  it('refuses anything that is not a GLB', () => {
    expect(extractGlbJson(new ArrayBuffer(8))).toBeNull();
    const notGlb = new ArrayBuffer(20);
    new DataView(notGlb).setUint32(0, 0x12345678, true);
    expect(extractGlbJson(notGlb)).toBeNull();
    expect(extractGlbJson(glbFile('not json'))).toBeNull();
  });
});

describe('parseGlbAnchors', () => {
  it('reads anchor positions through the parent chain', () => {
    const gltf = {
      nodes: [
        { name: 'room', translation: [1, 0, 0], children: [1] },
        { name: 'anchor_wall_1', translation: [0, 2, 3] },
        { name: 'static_toy_shelf' },
      ],
    };

    const anchors = parseGlbAnchors(gltf);

    expect(anchors).toHaveLength(1);
    expect(anchors[0].name).toBe('anchor_wall_1');
    expect(anchors[0].section).toBe('wall');
    expect(anchors[0].index).toBe(1);
    expect(anchors[0].position).toEqual([1, 2, 3]);
  });

  it('reads the aisle yaw off an anchor turned to face it', () => {
    // A quarter turn about Y: local -Z lands on world -X, so the yaw is -90 degrees.
    const quarter = Math.SQRT1_2;
    const gltf = { nodes: [{ name: 'anchor_case_1', rotation: [0, quarter, 0, quarter] }] };

    const [anchor] = parseGlbAnchors(gltf);

    expect(anchor.yaw).toBeCloseTo(-Math.PI / 2, 5);
  });

  it('yields nothing for static geometry and malformed documents', () => {
    expect(parseGlbAnchors({ nodes: [{ name: 'sign_GAMES' }] })).toEqual([]);
    expect(parseGlbAnchors(null)).toEqual([]);
    expect(parseGlbAnchors({})).toEqual([]);
  });
});

describe('glbStats', () => {
  it('counts draws and triangles across primitives', () => {
    const gltf = {
      nodes: [{ name: 'anchor_wall_1' }],
      meshes: [{ primitives: [{ indices: 0 }, { attributes: { POSITION: 1 } }] }],
      accessors: [{ count: 30 }, { count: 12 }],
    };

    expect(glbStats(gltf)).toEqual({ draws: 2, tris: 14, anchors: ['anchor_wall_1'] });
  });

  it('skips non-triangle primitives', () => {
    const gltf = {
      meshes: [{ primitives: [{ mode: 1, attributes: { POSITION: 0 } }] }],
      accessors: [{ count: 30 }],
    };

    expect(glbStats(gltf)).toEqual({ draws: 0, tris: 0, anchors: [] });
  });
});

describe('Blender to three.js angles', () => {
  it('turns Z-up presets into Y-up camera positions', () => {
    expect(blenderToThree([1, -4.2, 1.8])).toEqual([1, 1.8, 4.2]);
  });

  it('turns a lens into a field of view', () => {
    expect(lensToFov(30)).toBeCloseTo(43.6, 1);
  });
});
