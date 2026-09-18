import { MantineProvider } from '@mantine/core';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { beforeEach, expect, it, vi } from 'vitest';
import { Activity } from './Activity';

const harness = vi.hoisted(() => ({
  remove: vi.fn(), clear: vi.fn(), next: vi.fn(),
  sessions: [] as Array<{ sessionId: string; titleId: string; clientId: string; startedAt: string; endedAt: string | null; activeDurationSeconds: number | null }>,
}));
vi.mock('../hooks/usePlayActivity', () => ({
  usePlaySessions: () => ({ data: { pages: [{ items: harness.sessions }] }, hasNextPage: true, fetchNextPage: harness.next }),
  useDeletePlaySession: () => ({ mutateAsync: harness.remove }),
  useClearPlaySessions: () => ({ mutateAsync: harness.clear }),
}));
vi.mock('../hooks/useConsumerLibrary', () => ({
  useTitleDetail: () => ({ data: { name: 'Super Mario Kart', system: { key: 'snes', name: 'Super Nintendo Entertainment System', compactLabel: 'SNES' }, artwork: [] } }),
}));
beforeEach(() => {
  vi.clearAllMocks();
  harness.sessions = [{ sessionId: 'session-1', titleId: 'title-1', clientId: 'romd.web', startedAt: '2026-09-14T20:00:00Z', endedAt: null, activeDurationSeconds: null }];
});
it('shows game identity and honest duration labels instead of raw IDs', () => {
  render(<MantineProvider env="test"><MemoryRouter><Activity /></MemoryRouter></MantineProvider>);
  expect(screen.getByRole('table', { name: 'Play history' })).toBeInTheDocument();
  expect(screen.getByRole('link', { name: 'Super Mario Kart' })).toHaveAttribute('href', '/titles/title-1');
  expect(screen.getByText('Super Nintendo Entertainment System')).toBeInTheDocument();
  expect(screen.getByText('Browser')).toBeInTheDocument();
  expect(screen.getByText('Not recorded')).toBeInTheDocument();
  expect(screen.getByText('End not recorded')).toBeInTheDocument();
  expect(screen.queryByText('romd.web')).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Load older sessions' }));
  expect(harness.next).toHaveBeenCalledOnce();
});
it('keeps short recorded sessions distinct from unknown durations', () => {
  harness.sessions[0].activeDurationSeconds = 12;
  harness.sessions[0].endedAt = '2026-09-14T20:00:12Z';
  render(<MantineProvider env="test"><MemoryRouter><Activity /></MemoryRouter></MantineProvider>);
  expect(screen.getByText('12s')).toBeInTheDocument();
  expect(screen.queryByText('End not recorded')).not.toBeInTheDocument();
});
it('keeps a failed deletion open for retry and never clears all history', async () => {
  harness.remove.mockRejectedValueOnce(new Error('offline')).mockResolvedValueOnce(undefined);
  render(<MantineProvider env="test"><MemoryRouter><Activity /></MemoryRouter></MantineProvider>);
  fireEvent.click(screen.getByRole('button', { name: 'Session options for Super Mario Kart' }));
  fireEvent.click(await screen.findByRole('menuitem', { name: 'Remove from history' }));
  expect(harness.remove).not.toHaveBeenCalled();
  fireEvent.click(await screen.findByRole('button', { name: 'Remove session' }));
  expect(await screen.findByText(/Your history could not be updated/)).toBeInTheDocument();
  fireEvent.click(await screen.findByRole('button', { name: 'Remove session' }));
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  expect(harness.remove).toHaveBeenCalledWith('session-1');
  expect(harness.clear).not.toHaveBeenCalled();
});
it('offers the library when there is no history', () => {
  harness.sessions = [];
  render(<MantineProvider env="test"><MemoryRouter><Activity /></MemoryRouter></MantineProvider>);
  expect(screen.getByRole('link', { name: 'Browse games' })).toHaveAttribute('href', '/library');
  expect(screen.queryByRole('button', { name: 'Manage history' })).not.toBeInTheDocument();
});
