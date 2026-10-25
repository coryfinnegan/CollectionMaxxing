import { fireEvent, render, screen } from '@testing-library/react-native';
import { Cover } from './Cover';

describe('Cover', () => {
  it('falls back to the title tile when the image fails to load', async () => {
    await render(<Cover title="Dune" uri="https://example.invalid/cover.jpg" />);
    expect(screen.getByLabelText('Cover of Dune')).toBeTruthy();

    await fireEvent(screen.getByLabelText('Cover of Dune'), 'error');

    expect(screen.queryByLabelText('Cover of Dune')).toBeNull();
    expect(screen.getByText('Dune', { includeHiddenElements: true })).toBeTruthy();
  });

  it('shows the image again for a new address after a failure', async () => {
    const rendered = await render(<Cover title="Dune" uri="https://example.invalid/old.jpg" />);
    await fireEvent(screen.getByLabelText('Cover of Dune'), 'error');
    expect(screen.queryByLabelText('Cover of Dune')).toBeNull();

    await rendered.rerender(<Cover title="Dune" uri="https://example.invalid/new.jpg" />);

    expect(screen.getByLabelText('Cover of Dune')).toBeTruthy();
  });

  it('shows the title tile when there is no cover address', async () => {
    await render(<Cover title="Dune" />);

    expect(screen.getByText('Dune', { includeHiddenElements: true })).toBeTruthy();
  });
});
