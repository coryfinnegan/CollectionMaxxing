// The GLB seam (#381, #357): the shipped room files carry exactly the anchors
// the registry promises, inside the triangle and draw budgets. Blender edits
// that add, rename, or drop an anchor fail here until rooms.ts follows.
import { readFileSync } from 'fs';
import { join } from 'path';
import { extractGlbJson, glbStats } from './glb';
import { mallRooms, roomAnchors } from './rooms';

const MAX_TRIS = 150_000;
const MAX_DRAWS = 40;

function roomFile(room: string): ArrayBuffer {
  const bytes = readFileSync(join(__dirname, '../../../assets/stores', `${room}.glb`));
  return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
}

describe('the shipped room files', () => {
  it.each([...mallRooms])('carries the registered anchors for %s', (room) => {
    const json = extractGlbJson(roomFile(room));

    expect(json).not.toBeNull();
    expect(glbStats(json).anchors).toEqual([...roomAnchors[room]].sort());
  });

  it.each([...mallRooms])('stays inside the budgets for %s', (room) => {
    const stats = glbStats(extractGlbJson(roomFile(room)));

    expect(stats.tris).toBeLessThan(MAX_TRIS);
    expect(stats.draws).toBeLessThan(MAX_DRAWS);
  });
});
