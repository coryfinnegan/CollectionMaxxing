// The Mall room registry (#381): presets load, anchors round-trip, and the
// scene falls back to procedural geometry when the GLB is not bundled.
import { anchorName, mallRooms, parseAnchorName, resolveRoomSource, roomAnchorSections, roomPresets } from './rooms';

describe('the Mall room presets', () => {
  it('covers every room with a hero angle first', () => {
    expect([...mallRooms]).toEqual(['video_store', 'toy_store', 'bookstore', 'mall_hub']);
    for (const room of mallRooms) {
      expect(roomPresets[room].length).toBeGreaterThanOrEqual(1);
      expect(roomPresets[room][0].name).toBe('hero');
      for (const preset of roomPresets[room]) {
        expect(preset.location).toHaveLength(3);
        expect(preset.target).toHaveLength(3);
        expect(typeof preset.lens).toBe('number');
      }
    }
  });
});

describe('the anchor contract', () => {
  it('names every cover row anchor_<section>_<n>', () => {
    expect(anchorName('ticket', 2)).toBe('anchor_ticket_2');
  });

  it('parses a tapped anchor back to its cover row', () => {
    expect(parseAnchorName('anchor_ticket_2')).toEqual({ section: 'ticket', index: 2 });
  });

  it('ignores static geometry when picking', () => {
    expect(parseAnchorName('static_toy_shelf')).toBeNull();
    expect(parseAnchorName('sign_GAMES')).toBeNull();
    expect(parseAnchorName('')).toBeNull();
  });

  it('furnishes every section the scene graph expects per room', () => {
    expect(roomAnchorSections).toEqual({
      video_store: ['shelf', 'new', 'counter'],
      toy_store: ['ticket', 'case', 'peg'],
      bookstore: ['stack', 'mezz', 'cafe', 'table'],
      mall_hub: ['store', 'kiosk'],
    });
  });
});

describe('room source resolution', () => {
  it('loads the GLB when the bundler has it', () => {
    expect(resolveRoomSource(42)).toEqual({ kind: 'glb', asset: 42 });
  });

  it('falls back to procedural geometry when the GLB is missing', () => {
    expect(resolveRoomSource(null)).toEqual({ kind: 'procedural' });
    expect(resolveRoomSource(undefined)).toEqual({ kind: 'procedural' });
  });
});
