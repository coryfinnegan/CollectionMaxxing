// The bearer token goes only to this server's own artwork route, never to another route on it that a hostile path could reach.
import type { CandidateDto } from '@/api/generated/models';
import { compareArtwork, ownArtworkUrl } from './artStep';
import { FakeArt } from './testKit';

describe('ownArtworkUrl', () => {
  it.each(['/media/covers/aaaaaaaa-0000-4000-8000-000000000001', '/media/covers/abc.jpg', '/media/x'])('accepts the normalised media path %s', (path) => {
    expect(ownArtworkUrl(path)).toBe(`http://api.test${path}`);
  });

  it.each([
    ['dot-dot segments', '/media/../api/v1/me'],
    ['dot-dot at the end', '/media/covers/..'],
    ['a dot segment', '/media/./covers/abc'],
    ['encoded dot-dot', '/media/%2e%2e/api/v1/me'],
    ['upper-case encoded dot-dot', '/media/%2E%2E/api/v1/me'],
    ['half-encoded dot-dot', '/media/.%2e/api/v1/me'],
    ['double-encoded dot-dot', '/media/%252e%252e/api/v1/me'],
    ['an encoded slash', '/media/covers%2f..%2fapi'],
    ['an encoded backslash', '/media/covers%5c..%5capi'],
    ['a backslash', '/media/covers\\..\\api'],
    ['the API itself', '/api/v1/me'],
    ['a prefix that only looks like media', '/mediax/covers/abc'],
    ['the media folder alone', '/media'],
    ['an empty segment', '/media//api/v1/me'],
    ['a protocol-relative address', '//images.example.com/media/x'],
    ['a full address', 'https://images.example.com/media/x'],
    ['a relative path', 'media/covers/abc'],
    ['a malformed escape', '/media/covers/%zz'],
    ['a control character', '/media/covers/a\nb'],
  ])('refuses %s', (_name, path) => {
    expect(ownArtworkUrl(path)).toBeUndefined();
  });
});

describe('compareArtwork with a path that climbs out of /media', () => {
  it('downloads nothing, so the sign-in never goes to /api', async () => {
    const art = new FakeArt();
    const candidate = {
      alternatives: [
        { releaseId: 'r1', artworkUrl: '/media/covers/ok' },
        { releaseId: 'r2', artworkUrl: '/media/../api/v1/me' },
      ],
    } as unknown as CandidateDto;

    await expect(compareArtwork(art.deps, candidate, { uri: 'file:///snaps/crop.jpg' })).resolves.toBeUndefined();
    expect(art.downloads.map((d) => d.url).filter((url) => url.includes('/api/'))).toEqual([]);
  });
});
