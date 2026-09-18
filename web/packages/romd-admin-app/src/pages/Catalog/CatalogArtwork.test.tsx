import { MantineProvider } from '@mantine/core';
import type { CatalogTitle } from '@romd/admin-api-client';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { CatalogCard } from './CatalogCard';
import { CatalogRow } from './CatalogRow';

vi.mock('../../hooks/api/usePlatforms', () => ({ usePlatforms: () => ({ data: [] }) }));

vi.mock('../../hooks/api/useTitleActions', () => ({
  useSetTitleTracking: () => ({ mutate: vi.fn(), isPending: false }),
}));

const title: CatalogTitle = {
  id: 'title', systemKey: 'platform', name: 'Example Game', coverUrl: '/media/old-cover',
  artwork: [{
    role: 'Poster', assetId: 'asset', contentVersion: 'poster-v1', url: '/artwork/asset/poster/poster-v1',
    width: 600, height: 900, originalWidth: 600, originalHeight: 900,
    fit: 'Cover', fallbackReason: 'None', variants: [],
  }],
};

function Row(props: { title: CatalogTitle }) { return <table><tbody><CatalogRow {...props} /></tbody></table>; }
describe.each([{ name: 'card', Component: CatalogCard }, { name: 'row', Component: Row }])('Catalog $name artwork', ({ name, Component }) => {
  it('uses the shared picker artwork frame and resolved poster rather than coverUrl', () => {
    const { container } = render(<MantineProvider><MemoryRouter><Component title={title} /></MemoryRouter></MantineProvider>);
    expect(screen.getByRole('img', { name: 'Example Game poster' })).toHaveAttribute('src', '/artwork/asset/poster/poster-v1');
    // Mantine Image applies object-fit through this CSS variable.
    expect(screen.getByRole('img')).toHaveStyle({ '--image-object-fit': 'cover' });
    expect(screen.getByRole('img')).toHaveAttribute('loading', 'lazy');
    expect(container.innerHTML).not.toContain('/media/old-cover');
  });
  it('preserves the server-contained box art fallback', () => {
    const fallback = { ...title, artwork: [{ ...title.artwork![0], url: '/media/box', fit: 'Contain', fallbackReason: 'LegacyCover' }] };
    render(<MantineProvider><MemoryRouter><Component title={fallback} /></MemoryRouter></MantineProvider>);
    expect(screen.getByRole('img')).toHaveAttribute('src', '/media/box');
    expect(screen.getByRole('img')).toHaveStyle({ '--image-object-fit': 'contain' });
  });
  it('shows a placeholder when the server resolves no poster', () => {
    render(<MantineProvider><MemoryRouter><Component title={{ ...title, artwork: [] }} /></MemoryRouter></MantineProvider>);
    expect(name === 'row' ? screen.getByLabelText('No poster') : screen.getByText('No poster')).toBeInTheDocument();
    expect(screen.queryByRole('img')).toBeNull();
  });
});

it('counts logo coverage from resolved artwork and ignores empty URLs', () => {
  const value = { ...title, artwork: [...title.artwork!, { ...title.artwork![0], role: 'Hero', url: null }, { ...title.artwork![0], role: 'Logo', url: '/artwork/logo' }] };
  render(<MantineProvider><MemoryRouter><Row title={value} /></MemoryRouter></MantineProvider>);
  expect(screen.getByLabelText('Artwork: poster available, backdrop missing, banner missing, logo available')).toHaveTextContent('2/4');
});
