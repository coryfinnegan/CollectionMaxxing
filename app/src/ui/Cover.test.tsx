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

  it('tags the image for the shared transition when asked', async () => {
    await render(<Cover title="Dune" uri="https://example.invalid/cover.jpg" sharedTag="cover-1" />);

    expect(screen.getByLabelText('Cover of Dune').props.sharedTransitionTag).toBe('cover-1');
  });

  it('tags the title tile for the shared transition when asked', async () => {
    await render(<Cover title="Dune" sharedTag="cover-1" />);

    const tile = screen.getByText('Dune', { includeHiddenElements: true }).parent!;
    expect(tile.props.sharedTransitionTag).toBe('cover-1');
  });

  it('leaves the tag unset by default', async () => {
    await render(<Cover title="Dune" uri="https://example.invalid/cover.jpg" />);

    expect(screen.getByLabelText('Cover of Dune').props.sharedTransitionTag).toBeUndefined();
  });

  it('keeps a narrow tile to one line so words never break mid-word', async () => {
    await render(<Cover title="Code 97127666" width={44} />);

    expect(screen.getByText('Code 97127666', { includeHiddenElements: true }).props.numberOfLines).toBe(1);
  });

  it('wraps a wide tile over three lines', async () => {
    await render(<Cover title="The Legend of Zelda: Twilight Princess 9" width={96} />);

    expect(screen.getByText('The Legend of Zelda: Twilight Princess 9', { includeHiddenElements: true }).props.numberOfLines).toBe(3);
  });
});
