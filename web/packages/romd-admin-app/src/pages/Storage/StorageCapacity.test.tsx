import { client } from '@romd/admin-api-client';
import { screen } from '@testing-library/react';
import { HttpResponse, http } from 'msw';
import { afterEach, beforeEach, expect, it } from 'vitest';
import { operationalDiagnosticsFixture } from '../../test/msw/fixtures/diagnostics';
import { server } from '../../test/msw/server';
import { render } from '../../test/utils/render';
import { StorageCapacity } from './StorageCapacity';

beforeEach(() => client.setConfig({ baseUrl: 'http://localhost' }));
afterEach(() => client.setConfig({ baseUrl: '' }));
it('shows measured capacity and links to dependency evidence', async () => {
  render(<StorageCapacity />);
  expect(await screen.findByText('50 GB free on data volume')).toBeInTheDocument();
  expect(screen.getByRole('link', { name: 'Inspect storage dependencies and maintenance jobs' })).toHaveAttribute('href', '/diagnostics');
});
it('does not interpret an unavailable probe as measured free space', async () => {
  server.use(http.get('*/api/diagnostics/operational', () => HttpResponse.json({ ...operationalDiagnosticsFixture, storage: { dataVolumeAvailability: 'Unavailable', casAvailability: 'Unavailable', dataVolumeFreeBytes: '0' } })));
  render(<StorageCapacity />);
  expect(await screen.findByText('Free space unavailable')).toBeInTheDocument();
  expect(screen.queryByText('0 B free on data volume')).not.toBeInTheDocument();
});
