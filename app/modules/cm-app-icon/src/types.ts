/** Switching the home screen icon between the default dragon and the registered alternates (#364). */
export interface AppIconEngine {
  /** False where alternates don't exist (the web, Expo Go, an iPhone that declines them). */
  supportsAlternateIcons(): Promise<boolean>;
  /** The active alternate's plist name, or null for the default icon. */
  currentIconName(): Promise<string | null>;
  /** Applies the alternate, or null for the default icon. Rejects when the switch fails. */
  setIconName(name: string | null): Promise<void>;
}
