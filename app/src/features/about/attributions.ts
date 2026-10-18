// Text credits only (owner decision, 2026-10-17): no logo files. One entry per provider the server really uses.
export type Attribution = { name: string; notice: string; url: string };

export const ATTRIBUTIONS: Attribution[] = [
  {
    name: 'TMDB',
    notice: 'Movie and TV data from TMDB. This product uses the TMDB API but is not endorsed or certified by TMDB.',
    url: 'https://www.themoviedb.org',
  },
  { name: 'IGDB', notice: 'Game data from IGDB.com.', url: 'https://www.igdb.com' },
  { name: 'Open Library', notice: 'Book data from Open Library.', url: 'https://openlibrary.org' },
  { name: 'Google Books', notice: 'Book data from Google Books.', url: 'https://books.google.com' },
  { name: 'UPCitemdb', notice: 'Barcode lookups from UPCitemdb.com.', url: 'https://www.upcitemdb.com' },
];
