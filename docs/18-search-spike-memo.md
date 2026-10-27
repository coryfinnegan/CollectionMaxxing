# Dedicated Search view design-spike memo (#443)

Research + design for a dedicated Search view with work-grouped results.
No build code in this spike; the build is the filed issues at the bottom,
in dependency order.

## What exists today (the design is a delta, not a rewrite)

- `GET /api/v1/catalog/search` already returns work-grouped results:
  `CatalogSearchResultDto(Work, Releases[], Score, Source)`. Local hits are
  grouped by work server-side (`SearchCatalog.cs`); providers (Google Books,
  Open Library, TMDB, IGDB, UpcItemDb) each return one single-release
  "work" with `Guid.Empty` ids (`CatalogProjection.ToWorkDto`), so provider
  editions arrive as edition spam with identical titles.
- Both app call sites flatten the grouping: `AddItemPanel.tsx` (`SearchStep`)
  and `ScannerSearch.tsx` (`scope: 'library'`) do
  `results.flatMap(r => r.releases)` into bare `ListRow(title, format · year)`
  rows with no cover, no creator, no owned state. That flatMap is the bug
  Cory reported: the same title with different years, nothing to tell rows
  apart.
- Owned answers exist but are not in the response: `scope=library` re-ranks
  owned releases first via `MyReleaseIdsAsync`, and `ScannerSearch` joins an
  offline owned cache client-side. There is no "in your library" badge data.
- The Works model is real (`works` table with `external_ids` for
  `tmdb`/`igdb`/`openlibrary_work`, trigram index; docs/03). The search view
  is where that model finally becomes visible.

## Research (named sources, adopt vs beat)

### Open Library: search lists works, one work page rolls up editions

Source: https://blog.openlibrary.org/2020/07/08/re-thinking-open-librarys-book-pages/
("Works — logical groupings of similar editions ... perform a search for a
work of interest, navigate to a Work Page, and then choose"),
https://openlibrary.blog/ ("hardcover, paperback, translated, and revised
versions are all connected under one master record"),
https://blog.openlibrary.org/2022/12/21/search-is-getting-smarter-on-open-library/
(results prefer the edition matching the reader: representative-cover
selection and edition-aware query fields).

Adopt: one row per work in results; the editions table lives one tap down;
each row shows a representative cover. Beat: OL's representative edition is
language-driven for readers; ours is ownership-driven for collectors (your
edition's cover first, then the best-metadata edition).

### Goodreads: default edition in results, "Other editions" one tap down

Sources: Goodreads help ("the new edition was set as default already, so
that's the one that will show on search results ... all older editions will
be seen on the 'Other editions' page",
https://help.goodreads.com/s/question/0D51H00004RLZG5SAP/ive-recently-created-alternate-versions-of-my-book-with-updated-information-how-do-i-delete-the-older-versions);
app flow ("tap 'more details and editions'",
https://help.goodreads.com/s/question/0D58V00008C1FlJSAV/how-can-i-view-other-editions-of-a-book-on-the-goodreads-app).

Adopt: the two-level contract (result row = work + default edition face;
editions list = the exact pick). Beat: Goodreads buries the switch ("you
can't filter search results in any way, including by book format") and never
shows an edition count on the row; our row shows the edition count and the
editions list is the primary tap target, not a sidebar link.

### The StoryGraph: at most one edition per book in results, "Browse editions" page

Source: staff answer "There shouldn't be more than one edition of a book
showing up in the results page ... you'll be able to browse all editions of
the same book on one page, and select the format you're interested in"
(https://roadmap.thestorygraph.com/requests-ideas/posts/show-book-format-on-results-pafe);
edition pages show "37 editions" counts with format/language/publisher per
edition (https://app.thestorygraph.com/reading_challenge_prompts/92c44224-e93d-48cd-93e1-bde8865f77ce).

Adopt: the hard invariant — search results never contain two rows for the
same work — plus per-edition format/publisher rows and a visible edition
count. Beat: StoryGraph tracks reading status, not physical copies; our
editions list is copy-aware (owned badge per edition, "you own this on VHS"
vs "this DVD is new to you").

### CLZ: Core search by title+creator with covers, duplicate detection

Sources: "Search our CLZ Core by author and title ... automatically provides
cover images and full book details" (Google Play listing
`com.collectorz.javamobile.android.books`); "It knows your editions belong
to the same movie. Add a dupe and it tells you" but "the collection view
still shows every edition as its own flat row"
(https://moviestacks.com/best-physical-media-collection-trackers.html).

Adopt: title+creator querying with automatic covers and details, and
add-time duplicate/owned detection ("instantly see whether I already own
the title", https://clz.com/books/isbn-database). Beat: CLZ knows the
movie-to-edition link "under the hood" yet still renders flat rows; we
render the grouping. Also their scan flow needs a separate phone app; ours
is one app.

### Letterboxd: one page per film, year+director disambiguation, advanced triggers

Sources: advanced-search triggers (`director:`, `actor:`, year, combinable;
https://letterboxd.com/faq/ "Advanced search"); one canonical film page per
film (TMDB-backed; edition depth "Movie-level only" per the MovieStacks
comparison grid).

Adopt: year + creator disambiguation inside the row (remakes with the same
title are told apart by year and director), and combinable query narrowing
(our `kind`/`platform`/`year` params already exist server-side). Beat:
Letterboxd has no physical-edition concept at all — it tracks watched, not
owned copies. Our work row carries the owned badge and the editions list
carries formats; that is the whole product difference.

### GAMEYE (games reference): platform/region-first variants, brutal filtering

Source: "Filter by platform, Metacritic score, release type, release
region..." with 100+ platforms and manual tick-off lists (Google Play
listing `com.hairyharri.gameye`).

Adopt: for games, platform + region are the edition identity (not
publisher/year), and filters must include platform. Our `platforms`
endpoint and `platform` query param already exist; the Search view exposes
them as chips. Beat: GAMEYE is games-only; our one Search view spans books,
games, and movies/TV with per-type edition rows.

### Libib (anti-pattern): flat records, no work identity

Source: "It thinks in discs, not movies. Scan three different Batman Begins
editions and you get three unconnected records; it doesn't know they're the
same film" (https://moviestacks.com/best-physical-media-collection-trackers.html).
Also: "Searching the catalog drops the title straight into your collection
with no confirmation step."

This is exactly our current failure and the explicit non-goal: no flat
per-edition rows, and adding from search always passes through the editions
pick (a confirmation step), never a one-tap blind add.

## Data mapping: catalog responses to work vs edition, per media type

Local results already arrive as work + releases; provider hits need
grouping (build issue 1). Per-type edition identity:

- Books: edition = format + publisher + year (+ ISBN identifiers). Work key:
  provider's work id where available (Open Library work key), else
  normalized title + creators. Representatives: owned edition first, else
  first-scored.
- Games: edition = platform + region (+ format: cartridge/disc/digital).
  Work key: IGDB game id where available, else normalized title (+ platform
  family, so "Tetris (GB)" and "Tetris (NES)" stay distinct works only if
  the catalog models them so — default is one work, platform as edition).
- Movies/TV: edition = format (DVD/Blu-ray/4K/VHS) + publisher/label (+ year).
  Work key: TMDB id where available, else normalized title + year + director.
- UPC-only hits (UpcItemDb): single-release works, as today; they merge into
  local works via the existing `DuplicateKey` path when the catalog has them.

Server changes implied: group provider hits by work key server-side so every
client (Search view now, Review later) sees one row per work; expose owned
release ids in the response (the data `MyReleaseIdsAsync` already computes)
so rows render the badge without a second round trip. Ranking stays
score-ordered; exact-first and owned-first ordering is build issue 5, and
author-aware querying is #438 stage 1 — this epic consumes both, duplicates
neither.

## The design

A dedicated Search view: its own screen, replacing the results list in both
current call sites (`AddItemPanel` SearchStep, `ScannerSearch`). The
Library's own filter field is out of scope.

- Work row: cover (representative edition), title, creator, year span,
  edition count ("3 editions"), and an "In your library" badge when any
  edition is owned (owned edition's cover wins the slot). Same row component
  family as the Review candidate rows (#440): whichever lands first defines
  the shared component, the other reuses it — the edition-row shape must be
  identical in both places.
- Tap a work: its editions list (cover, year, format, publisher/platform per
  row) to pick the exact release. Picking continues the host flow (add to
  library / scanner match). Multi-pick is out of scope; one exact release
  per search task.
- Behavior: as-you-type with the existing debounce; recent searches on
  device; per-type filter chips driving the existing `kind`/`platform`/`year`
  params; ranking prefers exact title/creator matches, then owned/relevant.
- States: initial (recent searches + hint), typing (loading), results (work
  rows), work detail/editions, no results (empty state + "add it yourself"
  path, never a dead end), offline/error (owned-cache answers where they
  exist, per the `ScannerSearch` pattern, else the error line).

DESIGN.md constraints the build issues must respect: tokens only
(`useColors()` roles, no raw literals); `Text`/`ListRow`/`Cover`/`Chip`/
`TextField`/`LoadingState`/`EmptyState` primitives, StyleSheet not inline
styles; one title per screen (navigator header names the screen); sentence
case, verb-first buttons with counts, no exclamation marks; no mascot on
search results (banned surface); `select` haptic on filter changes, `tap`
on presses, `commit` on add.

## Routes evaluated

1. Group provider hits server-side (recommended): one row per work for every
   client, owned ids in the same response, Review reuse free. Cost: one
   server issue + a deliberate contract change with snapshot update.
2. Group client-side (rejected): every client re-implements work identity
   (provider keys differ per provider), Review would need it again, and
   owned state still needs the server round trip. Cheaper today, wrong twice.
3. New endpoint `works/search` (rejected): the current response shape is
   already work-grouped; a second endpoint doubles the contract for no new
   information. Extend, don't fork.

## Recommendation

Build route 1 in dependency order: server grouping + owned ids first, then
the shared row components, then the Search view screen adopted by both call
sites, then ranking. Ship the screen behind no flag — it replaces lists
Cory already called unusable.

## Follow-ups (filed, not started)

Costed `muse-task` issues under `epic:search`, dependency-ordered:

- #456 — Server: group provider hits into works; expose owned release ids
  in search results. Depends on: nothing. (Relates #438: queries stay there.)
- #457 — App: shared work-row + edition-row components. Depends on:
  #456 (grouped provider data + owned ids; local grouped results unblock
  early layout). Coordinate with #440: identical edition-row shape, shared
  component whichever lands first.
- #458 — App: dedicated Search view screen; adopt in AddItemPanel.
  Depends on: #456, #457.
- #459 — App: adopt the Search view in ScannerSearch (offline answers kept).
  Depends on: #458.
- #460 — Server: exact-first + owned-first result ranking. Depends on:
  #456. Relates #438 (author-aware querying stays there).

## What Cory gets back to us

Nothing — per the brief he is not a design gate. The work proceeds; he
files bugs on anything that lands wrong.
