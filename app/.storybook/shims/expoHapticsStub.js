// Storybook-only stub for expo-haptics. Expo SDK 57 ships TypeScript source as
// its entry point, which Metro compiles but Vite cannot bundle; and the app
// itself never reaches this module on web (every driver method returns early
// when Platform.OS === 'web'). The stub mirrors the used surface as no-ops so
// stories behave exactly as the app does on web.
export const ImpactFeedbackStyle = { Light: 'light', Medium: 'medium', Heavy: 'heavy' };
export const NotificationFeedbackType = { Success: 'success', Warning: 'warning', Error: 'error' };

export async function impactAsync() {}
export async function notificationAsync() {}
export async function selectionAsync() {}
