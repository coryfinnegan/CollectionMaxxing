import type { Preview } from '@storybook/react-vite';

// Primitives read useColors() (light/dark by media query) with no providers,
// so no decorators are needed. Keep global chrome out: stories show the
// primitive, not a page.
const preview: Preview = {
  parameters: { layout: 'centered' },
};

export default preview;
