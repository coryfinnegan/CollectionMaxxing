// Babel wiring for the Unistyles 3 styling layer (#305): the Expo preset plus the
// Unistyles plugin, scoped to src (the plugin refuses a root that resolves to the
// project dir). The plugin is a no-op under jest (NODE_ENV=test); tests use the
// documented mocks + config in jest.config.js setupFiles instead.
module.exports = function (api) {
  api.cache(true);
  return {
    presets: ['babel-preset-expo'],
    plugins: [['react-native-unistyles/plugin', { root: 'src' }]],
  };
};
