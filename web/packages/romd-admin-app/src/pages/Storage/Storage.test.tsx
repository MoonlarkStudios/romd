import { MantineProvider } from '@mantine/core';
import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Storage } from './index';

const { state, refetch } = vi.hoisted(() => ({ state: { available: true, isError: false, isPending: false }, refetch: vi.fn() }));
vi.mock('../../hooks/usePermissions', () => ({ usePermissions: () => ({ hasRole: () => false }) }));
vi.mock('../../hooks/api/useStorageStats', () => ({
  useStorageStats: () => ({
    data: state.available ? {
      totalStorageBytes: '4096', totalStorageBytesOnDisk: '1024',
      compressedFileCount: 3, uncompressedFileCount: 2,
      averageCompressionRatio: 4, bytesSaved: '3072',
    } : undefined,
    isError: state.isError, isPending: state.isPending, dataUpdatedAt: 0, refetch,
  }),
}));
beforeEach(() => { Object.assign(state, { available: true, isError: false, isPending: false }); });

function renderStorage() {
  return render(
    <MantineProvider>
      <Storage />
    </MantineProvider>,
  );
}

describe('Storage', () => {
  it('renders CAS size and compression metrics', () => {
    renderStorage();

    expect(screen.getByRole('heading', { name: 'Storage' })).toBeInTheDocument();
    expect(screen.getByText('1 KB')).toBeInTheDocument();
    expect(screen.getByText('4 KB')).toBeInTheDocument();
    expect(screen.getByText('3 KB')).toBeInTheDocument();
    expect(screen.getByText('5 registered objects')).toBeInTheDocument();
    expect(screen.getByText('4.00 compression ratio')).toBeInTheDocument();
    expect(screen.getByText('3')).toBeInTheDocument();
    expect(screen.getByText('2')).toBeInTheDocument();
  });
});


it('shows loading without invented zero metrics', () => {
  state.available = false; state.isPending = true;
  renderStorage();
  expect(screen.getByRole('status', { name: 'Loading storage' })).toBeInTheDocument();
  expect(screen.queryByText('0 B')).not.toBeInTheDocument();
});
it('shows an initial error without invented zero metrics', () => {
  state.available = false; state.isError = true;
  renderStorage();
  expect(screen.getByText('Could not load storage')).toBeInTheDocument();
  expect(screen.queryByText('Stored size')).not.toBeInTheDocument();
});
it('retains measured values when refresh fails', () => {
  state.isError = true;
  renderStorage();
  expect(screen.getByText('Could not refresh storage')).toBeInTheDocument();
  expect(screen.getByText('1 KB')).toBeInTheDocument();
});
