// E2E-only fixture (#457): the shared work and edition rows on fixed data, for
// the visual spec. Reached as /search-rows, which production builds redirect
// away from; nothing links here.
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import type { CatalogSearchResultDto, ReleaseDto } from '@/api/generated/models';
import { Screen, Text } from '@/ui';
import { EditionRow, WorkRow } from './rows';

/**
 * One inline cover per row (CSP allows data: images): a flat 2:3 tile with the
 * edition's initial. Base64, because the DOM layer re-encodes the escapes of a
 * plain SVG data URI and the image fails to load.
 */
const thumb = (art: string): string => `data:image/svg+xml;base64,${art}`;
const ART: Record<string, string> = {
  vhs: 'PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI0OCIgaGVpZ2h0PSI3MiI+PHJlY3Qgd2lkdGg9IjQ4IiBoZWlnaHQ9IjcyIiBmaWxsPSIjYjM1NDFlIi8+PHRleHQgeD0iMjQiIHk9IjQzIiBmb250LXNpemU9IjIyIiB0ZXh0LWFuY2hvcj0ibWlkZGxlIiBmaWxsPSIjZmZmZmZmIj5BPC90ZXh0Pjwvc3ZnPg==',
  dvd: 'PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI0OCIgaGVpZ2h0PSI3MiI+PHJlY3Qgd2lkdGg9IjQ4IiBoZWlnaHQ9IjcyIiBmaWxsPSIjMmU3ZDRmIi8+PHRleHQgeD0iMjQiIHk9IjQzIiBmb250LXNpemU9IjIyIiB0ZXh0LWFuY2hvcj0ibWlkZGxlIiBmaWxsPSIjZmZmZmZmIj5BPC90ZXh0Pjwvc3ZnPg==',
  bluray: 'PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI0OCIgaGVpZ2h0PSI3MiI+PHJlY3Qgd2lkdGg9IjQ4IiBoZWlnaHQ9IjcyIiBmaWxsPSIjM2U2YjRmIi8+PHRleHQgeD0iMjQiIHk9IjQzIiBmb250LXNpemU9IjIyIiB0ZXh0LWFuY2hvcj0ibWlkZGxlIiBmaWxsPSIjZmZmZmZmIj5BPC90ZXh0Pjwvc3ZnPg==',
  arrival: 'PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI0OCIgaGVpZ2h0PSI3MiI+PHJlY3Qgd2lkdGg9IjQ4IiBoZWlnaHQ9IjcyIiBmaWxsPSIjNWI1YmQ2Ii8+PHRleHQgeD0iMjQiIHk9IjQzIiBmb250LXNpemU9IjIyIiB0ZXh0LWFuY2hvcj0ibWlkZGxlIiBmaWxsPSIjZmZmZmZmIj5BPC90ZXh0Pjwvc3ZnPg==',
};

const ed = (n: number, workId: string, extra: Partial<ReleaseDto>): ReleaseDto => ({
  id: `e2000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
  workId,
  title: 'Alien',
  format: 'dvd',
  platform: null,
  region: null,
  edition: null,
  publisher: null,
  releaseDate: null,
  year: null,
  creators: ['Ridley Scott'],
  pageCount: null,
  runtimeMinutes: null,
  coverUrl: null,
  source: 'user',
  identifiers: [],
  ...extra,
});

const ALIEN = 'e1000000-0000-4000-8000-000000000001';
const alien: CatalogSearchResultDto = {
  work: {
    id: ALIEN,
    kind: 'film',
    title: 'Alien',
    originalYear: 1979,
    creators: ['Ridley Scott'],
    genres: [],
    synopsis: null,
    coverUrl: null,
  },
  releases: [
    ed(1, ALIEN, { format: 'vhs', year: 1979, coverUrl: thumb(ART.vhs!) }),
    ed(2, ALIEN, { format: 'dvd', year: 1999, publisher: 'Fox', coverUrl: thumb(ART.dvd!) }),
    ed(3, ALIEN, { format: 'bluray', year: 2007, edition: "Director's cut", publisher: 'Fox', coverUrl: thumb(ART.bluray!) }),
  ],
  score: 0.95,
  source: 'local',
  ownedReleaseIds: [],
};

const arrival: CatalogSearchResultDto = {
  work: { id: 'e1000000-0000-4000-8000-000000000002', kind: 'film', title: 'Arrival', originalYear: 2016, creators: ['Denis Villeneuve'], genres: [], synopsis: null, coverUrl: null },
  releases: [ed(4, 'e1000000-0000-4000-8000-000000000002', { title: 'Arrival', format: 'bluray', year: 2016, creators: ['Denis Villeneuve'], coverUrl: thumb(ART.arrival!) })],
  score: 0.9,
  source: 'local',
  ownedReleaseIds: [],
};

const dune: CatalogSearchResultDto = {
  work: { id: 'e1000000-0000-4000-8000-000000000003', kind: 'book', title: 'Dune', originalYear: 1965, creators: ['Frank Herbert'], genres: [], synopsis: null, coverUrl: null },
  releases: [
    ed(5, 'e1000000-0000-4000-8000-000000000003', { title: 'Dune', format: 'paperback', year: 1990, creators: ['Frank Herbert'] }),
    ed(6, 'e1000000-0000-4000-8000-000000000003', { title: 'Dune', format: 'hardcover', year: 2019, publisher: 'Ace', creators: ['Frank Herbert'] }),
  ],
  score: 0.85,
  source: 'local',
  ownedReleaseIds: [],
};

const nop = () => undefined;

export function SearchRowsFixture() {
  return (
    <Screen>
      <Text variant="heading" accessibilityRole="header">Works</Text>
      <View style={styles.rows}>
        <WorkRow result={alien} ownedReleaseIds={new Set([alien.releases[1]!.id])} onPress={nop} />
        <WorkRow result={arrival} ownedReleaseIds={new Set()} onPress={nop} />
        <WorkRow result={dune} ownedReleaseIds={new Set()} onPress={nop} />
      </View>
      <Text variant="heading" accessibilityRole="header">Editions of Alien</Text>
      <View style={styles.rows}>
        {alien.releases.map((release) => (
          <EditionRow key={release.id} release={release} ownedCount={release.id === alien.releases[1]!.id ? 1 : 0} onPress={nop} />
        ))}
      </View>
    </Screen>
  );
}

const styles = StyleSheet.create((theme) => ({
  rows: { gap: theme.spacing.sm },
}));
