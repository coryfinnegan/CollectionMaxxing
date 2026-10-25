// App entry (package.json main, #305): Expo Router first, then the Unistyles
// config, so StyleSheet.configure runs before any route module evaluates.
import 'expo-router/entry';
import './src/theme/unistyles';
