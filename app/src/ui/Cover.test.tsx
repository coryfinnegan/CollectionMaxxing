import { fireEvent, render, screen } from '@testing-library/react-native';
import { MemoryNativeAuth } from '@/lib/auth/native';
import { configureAuth, resetAuth } from '@/lib/auth/session';
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
});

describe('Cover for a cover this server names (#374)', () => {
  // The Legend of Zelda: Twilight Princess 9 (ISBN 9781974723386): the server fetched and stored its cover,
  // but the phone showed the gray tile because a relative /media path loads nowhere without its address and sign-in.
  const title = 'The Legend of Zelda: Twilight Princess 9';
  const label = `Cover of ${title}`;
  const zelda = '/media/covers/01a17621-c675-72d7-8d70-d3ecb91e2acf';

  afterEach(() => resetAuth());

  it('on the phone, asks this server for it, with the sign-in', async () => {
    configureAuth({ platform: 'native', native: new MemoryNativeAuth('tok') });

    await render(<Cover title={title} uri={zelda} />);

    const image = await screen.findByLabelText(label);
    expect(image.props.source).toEqual({ uri: `http://api.test${zelda}`, headers: { Authorization: 'Bearer tok' } });
  });

  it('on the phone, signs an already-absolute address of this server too', async () => {
    configureAuth({ platform: 'native', native: new MemoryNativeAuth('tok') });

    await render(<Cover title={title} uri={`http://api.test${zelda}`} />);

    const image = await screen.findByLabelText(label);
    expect(image.props.source).toEqual({ uri: `http://api.test${zelda}`, headers: { Authorization: 'Bearer tok' } });
  });

  it('on the phone, never sends the sign-in to another address', async () => {
    configureAuth({ platform: 'native', native: new MemoryNativeAuth('tok') });

    await render(<Cover title={title} uri="https://covers.openlibrary.org/b/id/14856981-L.jpg" />);

    const image = await screen.findByLabelText(label);
    expect(image.props.source).toEqual({ uri: 'https://covers.openlibrary.org/b/id/14856981-L.jpg' });
  });

  it('on the phone, never sends the sign-in to a server path that climbs out of /media', async () => {
    configureAuth({ platform: 'native', native: new MemoryNativeAuth('tok') });

    await render(<Cover title={title} uri="/media/../api/v1/me" />);

    const image = await screen.findByLabelText(label);
    expect(image.props.source).toEqual({ uri: '/media/../api/v1/me' });
  });

  it('on the web, keeps the server path as it is', async () => {
    configureAuth({ platform: 'web' });

    await render(<Cover title={title} uri={zelda} />);

    expect(screen.getByLabelText(label).props.source).toEqual({ uri: zelda });
  });
});
