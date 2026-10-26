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

describe('cm-design/no-unistyles-third-party-styles', () => {
  tester.run('no-unistyles-third-party-styles', plugin.rules['no-unistyles-third-party-styles'], {
    valid: [
      // #351: plain theme-valued objects at the third-party boundary (the #305 fix shape).
      "import { SafeAreaView } from 'react-native-safe-area-context'; const safe = { flex: 1 }; const el = <SafeAreaView style={safe} />;",
      "import { SafeAreaView } from 'react-native-safe-area-context'; const el = <SafeAreaView edges={['left', 'right']} style={{ flex: 1 }} />;",
      "import { TabSlot } from 'expo-router/ui'; const slot = { flex: 1 }; const el = <TabSlot style={slot} />;",
      "import { TabList, TabTrigger } from 'expo-router/ui'; const el = <TabList style={{ flexDirection: 'row' }}><TabTrigger name='a' style={{ flex: 1 }} /></TabList>;",
      "import { SafeAreaView as SAV } from 'react-native-safe-area-context'; const el = <SAV style={{ flex: 1 }} />;",
      "import { SafeAreaView } from 'react-native-safe-area-context'; const el = <SafeAreaView edges={['top']} />;",
      // Sheets still flow to hosts, Animated, and first-party components.
      "import { StyleSheet } from 'react-native-unistyles'; const styles = StyleSheet.create({ fill: { flex: 1 } }); const el = <View style={styles.fill} />;",
      'import { StyleSheet } from \'react-native-unistyles\'; const styles = StyleSheet.create({ bar: ({ width }) => ({ width }) }); const el = <View style={styles.bar({ width: 3 })} />;',
      "import { StyleSheet } from 'react-native-unistyles'; const styles = StyleSheet.create({ pill: { flex: 1 } }); const el = <GlassSurface style={styles.pill} />;",
      "import { StyleSheet } from 'react-native-unistyles'; const styles = StyleSheet.create({ panel: { flex: 1 } }); const el = <Animated.View style={styles.panel} />;",
      // A plain React Native sheet is not a Unistyles sheet: it resolves anywhere.
      "import { StyleSheet } from 'react-native'; import { SafeAreaView } from 'react-native-safe-area-context'; const styles = StyleSheet.create({ fill: { flex: 1 } }); const el = <SafeAreaView style={styles.fill} />;",
      // Same component name, but not the third-party import: not a boundary.
      "import { StyleSheet } from 'react-native-unistyles'; const SafeAreaView = View; const styles = StyleSheet.create({ fill: { flex: 1 } }); const el = <SafeAreaView style={styles.fill} />;",
    ],
    invalid: [
      // The (tabs)/_layout.tsx shape: a Unistyles sheet value into SafeAreaView.
      {
        code: "import { SafeAreaView } from 'react-native-safe-area-context'; import { StyleSheet } from 'react-native-unistyles'; const styles = StyleSheet.create({ fill: { flex: 1 } }); const el = <SafeAreaView style={styles.fill} />;",
        errors: 1,
      },
      {
        code: "import { TabSlot } from 'expo-router/ui'; import { StyleSheet } from 'react-native-unistyles'; const styles = StyleSheet.create({ slot: { flex: 1 } }); const el = <TabSlot style={styles.slot} />;",
        errors: 1,
      },
      {
        code: "import { TabList } from 'expo-router/ui'; import { StyleSheet } from 'react-native-unistyles'; const styles = StyleSheet.create({ bar: { flex: 1 } }); const el = <TabList style={styles.bar} />;",
        errors: 1,
      },
      {
        code: "import { TabTrigger } from 'expo-router/ui'; import { StyleSheet } from 'react-native-unistyles'; const styles = StyleSheet.create({ trigger: { flex: 1 } }); const el = <TabTrigger name='a' style={styles.trigger} />;",
        errors: 1,
      },
      // A dynamic sheet function crosses the same boundary.
      {
        code: "import { SafeAreaView } from 'react-native-safe-area-context'; import { StyleSheet } from 'react-native-unistyles'; const styles = StyleSheet.create({ tile: (w) => ({ w }) }); const el = <SafeAreaView style={styles.tile(width)} />;",
        errors: 1,
      },
      // Arrays, conditionals, aliases, and casts still carry the sheet value.
      {
        code: "import { SafeAreaView } from 'react-native-safe-area-context'; import { StyleSheet } from 'react-native-unistyles'; const styles = StyleSheet.create({ fill: { flex: 1 } }); const el = <SafeAreaView style={[styles.fill, { flex: 1 }]} />;",
        errors: 1,
      },
      {
        code: "import { SafeAreaView } from 'react-native-safe-area-context'; import { StyleSheet } from 'react-native-unistyles'; const styles = StyleSheet.create({ a: { flex: 1 }, b: { flex: 2 } }); const el = <SafeAreaView style={narrow ? styles.a : styles.b} />;",
        errors: 1,
      },
      {
        code: "import { SafeAreaView } from 'react-native-safe-area-context'; import { StyleSheet } from 'react-native-unistyles'; const styles = StyleSheet.create({ fill: { flex: 1 } }); const fill = styles.fill; const el = <SafeAreaView style={fill} />;",
        errors: 1,
      },
      {
        code: "import { SafeAreaView } from 'react-native-safe-area-context'; import { StyleSheet } from 'react-native-unistyles'; const styles = StyleSheet.create({ fill: { flex: 1 } }); const el = <SafeAreaView style={styles.fill as ViewStyle} />;",
        errors: 1,
      },
      // Sheets are usually declared at the bottom of the file, after the JSX.
      {
        code: "import { SafeAreaView } from 'react-native-safe-area-context'; import { StyleSheet } from 'react-native-unistyles'; const el = <SafeAreaView style={styles.fill} />; const styles = StyleSheet.create({ fill: { flex: 1 } });",
        errors: 1,
      },
      // Aliased imports on both sides.
      {
        code: "import { SafeAreaView as SAV } from 'react-native-safe-area-context'; import { StyleSheet as US } from 'react-native-unistyles'; const styles = US.create({ fill: { flex: 1 } }); const el = <SAV style={styles.fill} />;",
        errors: 1,
      },
    ],
  });
});
