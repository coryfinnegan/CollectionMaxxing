// Automock entry: the implementation lives next door so jest.config can also point
// `react-native-reanimated/mock` at it. (Pointing that path back here recurses:
// requiring this entry re-enters the mock factory for the same module.)
module.exports = require('./reanimated-impl');
