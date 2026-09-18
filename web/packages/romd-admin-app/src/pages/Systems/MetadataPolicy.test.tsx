import { MantineProvider } from '@mantine/core';
import { getPlatformFieldDefaults, setPlatformFieldDefaults } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { expect, it, vi } from 'vitest';
import { MetadataPolicy } from './MetadataPolicy';

vi.mock('../../hooks/usePermissions', () => ({ usePermissions: () => ({ canEditMetadataPolicy: true }) }));
vi.mock('@romd/admin-api-client', () => ({ getPlatformFieldDefaults: vi.fn(), setPlatformFieldDefaults: vi.fn() }));
it('deduplicates configured providers, loads saved preferences, and explicitly clears to Automatic', async () => {
  const initial = { revision: 'saved-revision', defaults: { Description: 'igdb' }, globalSourcePriority: ['igdb', 'igdb'], affectedTitles: 42 };
  vi.mocked(getPlatformFieldDefaults).mockResolvedValue({ data: initial } as Awaited<ReturnType<typeof getPlatformFieldDefaults>>);
  vi.mocked(setPlatformFieldDefaults).mockResolvedValue({ data: undefined } as Awaited<ReturnType<typeof setPlatformFieldDefaults>>);
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<MantineProvider env="test"><QueryClientProvider client={cache}><MemoryRouter><MetadataPolicy systemKey="system" /></MemoryRouter></QueryClientProvider></MantineProvider>);
  const user = userEvent.setup();
  const description = await screen.findByRole('textbox', { name: 'Description' });
  expect(description).toHaveValue('IGDB');
  fireEvent.click(description);
  await user.click(await screen.findByRole('option', { name: 'Automatic (igdb)' }));
  await user.click(screen.getByRole('button', { name: 'Save policy' }));
  await waitFor(() => expect(setPlatformFieldDefaults).toHaveBeenCalledWith({ path: { systemKey: 'system' }, body: { revision: 'saved-revision', defaults: { Description: '' } } }));
  expect(await screen.findByText(/durable request to recalculate/)).toBeInTheDocument();
});
