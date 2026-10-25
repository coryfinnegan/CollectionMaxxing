import { render, screen } from '@testing-library/react-native';
import { Text } from './Text';
import { typeScale } from '@/theme/tokens';

describe('Text', () => {
  it('renders body copy in the UI face by default', async () => {
    await render(<Text>Hi</Text>);
    expect(screen.getByText('Hi').props.style).toMatchObject([typeScale.body, { color: expect.any(String) }]);
  });

  it('renders catalog metadata in mono', async () => {
    await render(<Text variant="mono">1996 · N64</Text>);
    expect(screen.getByText('1996 · N64').props.style).toMatchObject([typeScale.mono, { color: expect.any(String) }]);
  });

  it('respects Dynamic Type', async () => {
    await render(<Text>Hi</Text>);
    expect(screen.getByText('Hi').props.allowFontScaling).not.toBe(false);
  });
});
