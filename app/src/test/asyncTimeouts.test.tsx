import { render, screen } from '@testing-library/react-native';
import { useEffect, useState } from 'react';
import { Text } from 'react-native';

// Regression guard for #53: findBy defaulted to 1s, which a cold, loaded machine (CI, parallel
// agents) blows through on a screen's first render. jest.setup.ts must raise it for every suite.
function SlowText() {
  const [ready, setReady] = useState(false);
  useEffect(() => {
    const timer = setTimeout(() => setReady(true), 1500);
    return () => clearTimeout(timer);
  }, []);
  return <Text>{ready ? 'ready' : 'loading'}</Text>;
}

describe('async test timeouts', () => {
  it('lets findBy wait longer than the one-second library default', async () => {
    await render(<SlowText />);

    expect(await screen.findByText('ready')).toBeTruthy();
  });
});
