// Jest stand-in for react-native-image-colors: palette extraction is native.
// Tests script `mockGetColors` through `jest.requireMock('react-native-image-colors')`.
// By default it rejects, so useCoverAccent caches "no accent" and screens render
// the token fallback (#306).
const mockGetColors = jest.fn(async () => {
  throw new Error('image colors unavailable in jest');
});

module.exports = {
  __esModule: true,
  getColors: mockGetColors,
  mockGetColors,
  default: { getColors: mockGetColors },
};
