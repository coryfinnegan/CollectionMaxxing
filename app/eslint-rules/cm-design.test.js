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
      // #331 carve-out: an inline object whose every value is dynamic may stay,
      // because a module-scope StyleSheet cannot express runtime values.
      'const el = <View style={{ width }} />;',
      'const el = <LottieView style={{ width: size, height: size }} />;',
      'const el = <View style={{ backgroundColor: colors.ground }} />;',
      'const el = <View style={{ width: getWidth() }} />;',
      "const el = <View style={{ flexDirection: wide ? 'row' : 'column' }} />;",
      // Passes ONLY because the inner translateY is dynamic — the static
      // twin below (translateY: 5) fails. The pair proves nested values are
      // inspected rather than waved through.
      'const el = <View style={{ transform: [{ translateY: shown ? 0 : -200 }] }} />;',
      'const el = <View style={{ width: `${w}px` }} />;',
      'const el = <View style={[styles.a, { backgroundColor: colors.accent }]} />;',
      'const el = <View style={(s) => ({ opacity: pressed ? 1 : 0.5 })} />;',
    ],
    invalid: [
      { code: 'const el = <View style={{ flex: 1 }} />;', errors: 1 },
      { code: 'const el = <View style={[styles.a, { flex: 1 }]} />;', errors: 1 },
      { code: 'const el = <ScrollView contentContainerStyle={{ padding: 8 }} />;', errors: 1 },
      { code: 'const el = <View style={(s) => [{ opacity: 1 }]} />;', errors: 1 },
      // #331 boundary: one static literal in the object still fails, even beside dynamic values.
      { code: 'const el = <View style={{ flex: 1, backgroundColor: colors.ground }} />;', errors: 1 },
      { code: 'const el = <View style={{ width: 44, height: 44 }} />;', errors: 1 },
      { code: 'const el = <View style={[styles.a, { width: barWidth, height: 16 }]} />;', errors: 1 },
      { code: 'const el = <View style={{ marginTop: -8 }} />;', errors: 1 },
      { code: 'const el = <View style={{ width: `100%` }} />;', errors: 1 },
      { code: 'const el = <View style={{ transform: [{ translateY: 5 }] }} />;', errors: 1 },
      { code: 'const el = <View style={{ shadowOffset: { width: 0, height: 2 } }} />;', errors: 1 },
      { code: "const el = <View style={{ flexDirection: 'row' as const }} />;", errors: 1 },
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
      'const el = <NativeTabs.Trigger.Label>Library</NativeTabs.Trigger.Label>;',
    ],
    invalid: [
      { code: "import { View } from 'react-native'; const el = <View>Hello</View>;", errors: 1 },
      { code: 'const el = <Foo.Bar>Label</Foo.Bar>;', errors: 1 },
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
