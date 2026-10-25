// A visible keyboard focus ring on the web (WCAG 2.4.7, 2.4.11), in both color schemes. It only shows for keyboard focus
// (:focus-visible), so mouse clicks stay clean. The style element is added at run time, which the page's CSP allows
// (`style-src 'unsafe-inline'`); no inline script is involved. The ring is the accent token, not a hard-coded hex.
import { darkColors, lightColors } from '@/theme/tokens';

const ID = 'cm-focus-ring';

export const FOCUS_RING_CSS = `
:focus-visible { outline: 3px solid ${lightColors.accent} !important; outline-offset: 2px !important; }
@media (prefers-color-scheme: dark) { :focus-visible { outline-color: ${darkColors.accent} !important; } }
[tabindex="-1"]:focus-visible { outline: none !important; }
`;

export function installFocusRing(): void {
  if (typeof document === 'undefined' || document.getElementById(ID)) return;
  const style = document.createElement('style');
  style.id = ID;
  style.textContent = FOCUS_RING_CSS;
  document.head.appendChild(style);
}
