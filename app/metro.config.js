// Metro config: the Expo defaults, plus the Mall room files (#357). The .glb
// rooms bundle as assets so the Mall scene can load them through expo-asset;
// nothing else about resolution changes.
const { getDefaultConfig } = require('expo/metro-config');

const config = getDefaultConfig(__dirname);
if (!config.resolver.assetExts.includes('glb')) {
  config.resolver.assetExts.push('glb');
}
module.exports = config;
