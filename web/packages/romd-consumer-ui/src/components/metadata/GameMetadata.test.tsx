import { MantineProvider } from '@mantine/core';
import { render as baseRender, fireEvent, screen } from '@testing-library/react';
import type { ReactElement } from 'react';
import { describe, expect, it } from 'vitest';
import { CatalogFixture, referenceCatalog } from '../../../../../test/referenceCatalog';
import { GameMetadata, PlatformMark, RatingMark } from './GameMetadata';

const render = (ui: ReactElement) => baseRender(ui, { wrapper: ({ children }) => <MantineProvider>{children}</MantineProvider> });
const rating = (board: string, code: string) => ({ ...referenceCatalog.ratings.find(item => item.board === board && item.code === code)!, boardName: board });

describe('shared game metadata', () => {
  it('renders a newly registered system and follows a changed snapshot', () => {
    const catalog = { revision: 'one', systems: [{ key: 'future', name: 'Future Console', compactLabel: 'FUT', icon: { url: '/immutable/one.png', sha256: 'one', contentType: 'image/png', monochrome: false } }], ratingBoards: [], ratings: [] };
    const view = (data: typeof catalog) => <PlatformMark system={data.systems[0]} />;
    const { container, rerender } = render(view(catalog));
    expect(screen.getByRole('button', { name: 'Future Console' })).toBeVisible();
    expect(container.querySelector('img')).toHaveAttribute('src', '/immutable/one.png');
    rerender(view({ ...catalog, revision: 'two', systems: [{ ...catalog.systems[0], name: 'Renamed Console', compactLabel: 'NEW', icon: { ...catalog.systems[0].icon, url: '/immutable/two.png' } }] }));
    expect(screen.getByRole('button', { name: 'Renamed Console' })).toBeVisible();
    expect(container.querySelector('img')).toHaveAttribute('src', '/immutable/two.png');
  });
  it('shows the supplied display name in a tooltip on keyboard focus', async () => {
    render(<PlatformMark system={{ key: 'snes', name: 'My Nintendo library', compactLabel: 'SNES', icon: { url: '/snes.png', sha256: 'snes', contentType: 'image/png', monochrome: true } }} />);
    expect(screen.queryByText('My Nintendo library')).toBeNull();
    fireEvent.focus(screen.getByRole('button', { name: 'My Nintendo library' }));
    expect(await screen.findByRole('tooltip')).toHaveTextContent('My Nintendo library');
  });
  it('falls back to the server name for an unknown platform and keeps text after an image failure', () => {
    const { rerender, container } = render(<PlatformMark system={{ key: 'custom', name: 'Custom platform', compactLabel: 'Custom platform' }} />);
    expect(screen.getByText('Custom platform')).toBeVisible();
    expect(container.querySelector('img')).toBeNull();
    rerender(<PlatformMark system={{ key: 'snes', name: 'Super Nintendo', compactLabel: 'SNES', icon: { url: '/snes.png', sha256: 'snes', contentType: 'image/png', monochrome: true } }} />);
    fireEvent.error(container.querySelector('img')!);
    expect(screen.getByText('Super Nintendo')).toBeVisible();
    expect(container.querySelector('img')).toBeNull();
  });
  it('honors a hidden icon even when the cached catalog still has artwork', () => {
    const { container } = render(<PlatformMark system={{ key: 'snes', name: 'My console', compactLabel: 'CUSTOM', icon: null }} />);
    expect(screen.getByText('My console')).toBeVisible();
    expect(container.querySelector('img')).toBeNull();
  });
  it('uses resource facts even when a surrounding catalog has stale artwork', () => {
    const { rerender } = baseRender(<CatalogFixture><RatingMark board="Esrb" code="E" name="Local classification" description="Local description" icon={null} /></CatalogFixture>);
    expect(screen.queryByRole('img')).toBeNull();
    expect(screen.getByText('Local classification')).toBeVisible();
    rerender(<RatingMark board="FutureBoard" code="X" name="Future rating" description="Future description" icon={{ url: '/api/assets/future', sha256: 'future', contentType: 'image/svg+xml', monochrome: false }} />);
    expect(screen.getByRole('img', { name: 'Future description' })).toHaveAttribute('src', '/api/assets/future');
  });
  it('never guesses a rating and retains accessible text if artwork fails', () => {
    const { rerender } = render(<RatingMark {...rating("Esrb", "E")} />);
    fireEvent.error(screen.getByRole('img', { name: 'ESRB Everyone' }));
    expect(screen.getByText('ESRB E')).toBeVisible();
    rerender(<RatingMark board="FutureBoard" code="X" />);
    expect(screen.getByText('FutureBoard X')).toBeVisible();
  });
  it('shows a whole-number score with its scale and preserves classification board identity', () => {
    render(<GameMetadata system={{ key: 'snes', name: 'SNES', compactLabel: 'SNES', icon: { url: '/snes.png', sha256: 'snes', contentType: 'image/png', monochrome: true } }} releaseDate="1994-03-19" genre="Adventure" players={1} rating={96.2} contentRatings={[rating('Pegi', 'PEGI 12')]} />);
    expect(screen.getByLabelText('Rating 96 out of 100')).toBeVisible();
    expect(screen.getByText('1 player')).toBeVisible();
    expect(screen.getByText('1994')).toBeVisible();
    expect(screen.getByRole('img', { name: 'PEGI 12' })).toBeVisible();
  });
  it.each([-1, 101, Number.NaN])('omits invalid scores and player counts (%s)', rating => {
    render(<GameMetadata system={{ key: 'unknown', name: 'Unknown', compactLabel: 'Unknown' }} players={0} rating={rating} />);
    expect(screen.queryByLabelText(/Rating .* out of/)).toBeNull();
    expect(screen.queryByText(/players?/)).toBeNull();
  });
  it('retains a real zero score', () => {
    render(<GameMetadata system={{ key: 'unknown', name: 'Unknown', compactLabel: 'Unknown' }} players={2} rating={0} />);
    expect(screen.getByLabelText('Rating 0 out of 100')).toBeVisible();
    expect(screen.getByText('2 players')).toBeVisible();
  });
});
