import { getAccountSecurity, listAccountLinks, listAccountSessions } from '@romd/admin-api-client';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { loadAccountSecurity } from './AccountSecurityPanel';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  getAccountSecurity: vi.fn(),
  listAccountLinks: vi.fn(),
  listAccountSessions: vi.fn(),
}));

describe('complete account security history', () => {
  beforeEach(() => vi.resetAllMocks());

  it('drains both embedded history pages in the selected account scope', async () => {
    const link = {
      id: 'link-1',
      purpose: 'recovery',
      createdAt: '2026-10-21T00:00:00Z',
      expiresAt: '2026-10-22T00:00:00Z',
      status: 'Revoked',
    };
    const session = {
      id: 'session-1',
      client: 'ROMD Console',
      device: 'Living room',
      status: 'Expired',
      lastUsedAt: '2026-10-21T00:00:00Z',
    };
    vi.mocked(getAccountSecurity).mockResolvedValue({
      data: {
        links: {
          items: [
            link,
          ],
          nextCursor: 'links-2',
          hasNextPage: true,
        },
        sessions: {
          items: [
            session,
          ],
          nextCursor: 'sessions-2',
          hasNextPage: true,
        },
      },
    });
    vi.mocked(listAccountLinks).mockResolvedValue({
      data: {
        items: [
          {
            ...link,
            id: 'link-2',
          },
        ],
        nextCursor: null,
        hasNextPage: false,
      },
    });
    vi.mocked(listAccountSessions).mockResolvedValue({
      data: {
        items: [
          {
            ...session,
            id: 'session-2',
          },
        ],
        nextCursor: null,
        hasNextPage: false,
      },
    });

    const result = await loadAccountSecurity('account-1');

    expect(result.links.map((item) => item.id)).toEqual([
      'link-1',
      'link-2',
    ]);
    expect(result.sessions.map((item) => item.id)).toEqual([
      'session-1',
      'session-2',
    ]);
    expect(listAccountLinks).toHaveBeenCalledExactlyOnceWith({
      path: {
        userId: 'account-1',
      },
      query: {
        cursor: 'links-2',
        limit: 100,
      },
    });
    expect(listAccountSessions).toHaveBeenCalledExactlyOnceWith({
      path: {
        userId: 'account-1',
      },
      query: {
        cursor: 'sessions-2',
        limit: 100,
      },
    });
  });

  it('fails the history load when a later session page fails', async () => {
    vi.mocked(getAccountSecurity).mockResolvedValue({
      data: {
        links: {
          items: [],
          nextCursor: null,
          hasNextPage: false,
        },
        sessions: {
          items: [],
          nextCursor: 'sessions-2',
          hasNextPage: true,
        },
      },
    });
    vi.mocked(listAccountSessions).mockResolvedValue({
      error: {
        title: 'Unavailable',
        status: 503,
      },
    });

    await expect(loadAccountSecurity('account-1')).rejects.toThrow(
      'complete account session history',
    );
  });
});
