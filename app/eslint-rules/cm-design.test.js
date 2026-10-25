// Behaviour tests for the design-ratchet rules (#307): raw colors, inline
// styles, raw text, doubled titles, and gradients must all fail in new code.
const { RuleTester } = require('eslint');
const tsParser = require('@typescript-eslint/parser');
const plugin = require('./cm-design');

const tester = new RuleTester({
  languageOptions: {
    parser: tsParser,
    ecmaVersion: 'latest',
    sourceType: 'module',
    parserOptions: { ecmaFeatures: { jsx: true } },
  },
});

describe('cm-design/no-raw-colors', () => {
  tester.run('no-raw-colors', plugin.rules['no-raw-colors'], {
    valid: [
      'const c = colors.accent;',
      "import { Text } from 'react-native'; const el = <Text>Add 11 items</Text>;",
      '// a #fff comment is not a color',
      "const label = 'Flash';",
    ],
    invalid: [
      { code: "const c = '#ff0000';", errors: 1 },
      { code: "const el = <View style={{ color: '#fff' }} />;", errors: 1 },
      { code: "const s = 'rgba(0,0,0,0.4)';", errors: 1 },
      { code: 'const s = `hsl(0, 0%, 50%)`;', errors: 1 },
    ],
  });
});

describe('cm-design/no-inline-styles', () => {
  tester.run('no-inline-styles', plugin.rules['no-inline-styles'], {
    valid: [
      'const el = <View style={styles.box} />;',
      'const el = <View style={[styles.a, styles.b]} />;',
      'const el = <ScrollView contentContainerStyle={padding} />;',
      'const el = <View />;',
    ],
    invalid: [
      { code: 'const el = <View style={{ flex: 1 }} />;', errors: 1 },
      { code: 'const el = <View style={[styles.a, { flex: 1 }]} />;', errors: 1 },
      { code: 'const el = <ScrollView contentContainerStyle={{ padding: 8 }} />;', errors: 1 },
      { code: 'const el = <View style={(s) => [{ opacity: 1 }]} />;', errors: 1 },
    ],
  });
});

describe('cm-design/no-raw-text', () => {
  tester.run('no-raw-text', plugin.rules['no-raw-text'], {
    valid: [
      "import { Text } from 'react-native'; const el = <Text>Hello</Text>;",
      "import { Text as RNText } from 'react-native'; const el = <RNText>Hi</RNText>;",
      "import { Text } from './Text'; const el = <Text>{label}</Text>;",
      "import { Text } from 'react-native'; const el = <View><Text>Hi</Text></View>;",
      "import { Text } from 'react-native'; const el = <View>{busy ? <Text>x</Text> : null}</View>;",
    ],
    invalid: [
      { code: "import { View } from 'react-native'; const el = <View>Hello</View>;", errors: 1 },
      { code: "import { View } from 'react-native'; const el = <View>{'Hello'}</View>;", errors: 1 },
      { code: 'const el = <Screen>Body copy</Screen>;', errors: 1 },
    ],
  });
});

describe('cm-design/no-doubled-title', () => {
  tester.run('no-doubled-title', plugin.rules['no-doubled-title'], {
    valid: [
      "const el = <Stack.Screen options={{ title: 'Library' }} />;",
      'const el = <Screen title="Library" />;',
      "const el = <><Stack.Screen options={{ title: 'Library' }} /><Screen title='Shelves' /></>;",
      "const el = <Screen title={name} />;",
    ],
    invalid: [
      {
        code: "const el = <><Stack.Screen options={{ title: 'Library' }} /><Screen title='Library' /></>;",
        errors: 1,
      },
      {
        code: 'const el = <><Stack.Screen options={{ headerTitle: "Scan" }} /><Text variant="title">Scan</Text></>;',
        errors: 1,
      },
    ],
  });
});

describe('cm-design/no-gradients', () => {
  tester.run('no-gradients', plugin.rules['no-gradients'], {
    valid: ['const el = <View style={styles.box} />;'],
    invalid: [
      { code: "import { LinearGradient } from 'expo-linear-gradient';", errors: 1 },
      { code: 'const el = <LinearGradient colors={["red", "blue"]} />;', errors: 1 },
    ],
  });
});
