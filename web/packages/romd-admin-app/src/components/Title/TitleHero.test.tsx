import { readFileSync } from 'node:fs';
import { MantineProvider } from '@mantine/core';
import type {
  ArchiveFormatDto,
  EnrichmentAssessmentDto,
  TitleDetail,
} from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { enrichmentWorkspaceKeys } from '../../hooks/api/enrichmentWorkspaceCache';
import { finalPage } from '../../test/workspacePages';
import classes from './TitleDetail.module.css';
import { TitleHero } from './TitleHero';

const permissions = vi.hoisted(() => ({
  canManageTitles: true,
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => permissions,
}));

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  getAdminSystem: vi.fn(),
  getTitleEnrichmentAssessment: vi.fn(),
  listTitleSourceReferences: vi.fn(),
  createDownloadTicket: vi.fn(),
}));
vi.mock('@romd/delivery', () => ({
  startBrowserDownload: vi.fn(),
}));

import {
  createDownloadTicket,
  getAdminSystem,
  getTitleEnrichmentAssessment,
  listTitleSourceReferences,
} from '@romd/admin-api-client';
import { startBrowserDownload } from '@romd/delivery';

type AssessmentResult = Awaited<ReturnType<typeof getTitleEnrichmentAssessment>>;
type Flags = Pick<
  EnrichmentAssessmentDto,
  'needsReview' | 'hasOperationalBlocker' | 'hasActiveWork' | 'canEnrichAutomatically'
>;

const title: TitleDetail = {
  id: 'chrono',
  systemKey: 'snes',
  name: 'Chrono Trigger',
  enrichmentStatus: 'Completed',
  genre: 'Role-playing (RPG)',
  releaseDate: '1995-03-11',
};
const original: ArchiveFormatDto = {
  format: 'original',
  label: 'Original',
};
const rvz: ArchiveFormatDto = {
  format: 'rvz',
  label: 'RVZ',
};
const chd: ArchiveFormatDto = {
  format: 'chd',
  label: 'CHD',
};
/** A title with a complete release, offering the archive formats the server lists. */
const ownedTitle = (archiveFormats: ArchiveFormatDto[]) => ({
  ...title,
  archiveFormats,
  releases: [
    {
      id: 'release',
      isComplete: true,
    },
  ],
});
const noFlags: Flags = {
  needsReview: false,
  hasOperationalBlocker: false,
  hasActiveWork: false,
  canEnrichAutomatically: false,
};
const assessment = (flags: Partial<Flags>): EnrichmentAssessmentDto => ({
  titleId: 'chrono',
  titleName: 'Chrono Trigger',
  platformName: 'Super Nintendo',
  revision: 'revision',
  ...noFlags,
  ...flags,
  requirements: [],
  acceptedOmissions: [],
});
const resolved = (data: EnrichmentAssessmentDto) =>
  ({
    data,
    error: undefined,
    response: new Response(),
  }) as AssessmentResult;

function renderHero(props: { compact?: boolean; title?: TitleDetail } = {}) {
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
        gcTime: 0,
      },
    },
  });
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <TitleHero
            title={title}
            titleId="chrono"
            {...props}
          />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
  return client;
}

const chipLabels = async () =>
  within(
    await screen.findByRole('group', {
      name: 'Enrichment status',
    }),
  )
    .getAllByText(/./)
    .map((chip) => chip.textContent);

describe('TitleHero status chips', () => {
  beforeEach(() => {
    permissions.canManageTitles = true;
    vi.mocked(getAdminSystem).mockResolvedValue({
      data: undefined,
      error: {},
      response: new Response(null, {
        status: 404,
      }),
    } as Awaited<ReturnType<typeof getAdminSystem>>);
    vi.mocked(listTitleSourceReferences).mockResolvedValue({
      data: finalPage([]),
      error: undefined,
      response: new Response(),
    } as Awaited<ReturnType<typeof listTitleSourceReferences>>);
    vi.mocked(getTitleEnrichmentAssessment).mockReset();
  });

  it.each<
    [
      string,
      Partial<Flags>,
      string[],
    ]
  >([
    [
      'scenario 8.1 mixed states',
      {
        needsReview: true,
        hasOperationalBlocker: true,
        hasActiveWork: true,
      },
      [
        'Needs review',
        'Blocked',
        'Processing',
      ],
    ],
    [
      'every flag',
      {
        needsReview: true,
        hasOperationalBlocker: true,
        hasActiveWork: true,
        canEnrichAutomatically: true,
      },
      [
        'Needs review',
        'Blocked',
        'Processing',
        'Automatic',
      ],
    ],
    [
      'blocked with automatic work',
      {
        canEnrichAutomatically: true,
        hasOperationalBlocker: true,
      },
      [
        'Blocked',
        'Automatic',
      ],
    ],
    [
      'needs review only',
      {
        needsReview: true,
      },
      [
        'Needs review',
      ],
    ],
    [
      'blocked only',
      {
        hasOperationalBlocker: true,
      },
      [
        'Blocked',
      ],
    ],
    [
      'processing only',
      {
        hasActiveWork: true,
      },
      [
        'Processing',
      ],
    ],
    [
      'automatic only',
      {
        canEnrichAutomatically: true,
      },
      [
        'Automatic',
      ],
    ],
    [
      'scenario 8.2 only optional assets missing',
      {},
      [
        'Settled',
      ],
    ],
  ])('renders %s in the fixed chip order', async (_name, flags, expected) => {
    vi.mocked(getTitleEnrichmentAssessment).mockResolvedValue(resolved(assessment(flags)));
    renderHero();
    expect(await chipLabels()).toEqual(expected);
    expect(getTitleEnrichmentAssessment).toHaveBeenCalledWith(
      expect.objectContaining({
        path: {
          titleId: 'chrono',
        },
      }),
    );
  });

  it('keeps status visible in the compact header while dropping descriptive details', async () => {
    vi.mocked(getTitleEnrichmentAssessment).mockResolvedValue(
      resolved(
        assessment({
          needsReview: true,
        }),
      ),
    );
    renderHero({
      compact: true,
    });
    expect(await chipLabels()).toEqual([
      'Needs review',
    ]);
    expect(screen.queryByText('Role-playing (RPG)')).not.toBeInTheDocument();
    expect(screen.queryByText('1995')).not.toBeInTheDocument();
  });

  it('renders no status while the assessment is loading', async () => {
    vi.mocked(getTitleEnrichmentAssessment).mockReturnValue(
      new Promise<AssessmentResult>(() => undefined),
    );
    const client = renderHero();
    expect(
      await screen.findByRole('heading', {
        name: 'Chrono Trigger',
      }),
    ).toBeInTheDocument();
    await waitFor(() => expect(getTitleEnrichmentAssessment).toHaveBeenCalled());
    expect(client.getQueryState(enrichmentWorkspaceKeys.title('chrono'))?.status).toBe('pending');
    expect(
      screen.queryByRole('group', {
        name: 'Enrichment status',
      }),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('Settled')).not.toBeInTheDocument();
  });

  it('renders no status when the assessment fails to load', async () => {
    vi.mocked(getTitleEnrichmentAssessment).mockResolvedValue({
      data: undefined,
      error: {},
      response: new Response(null, {
        status: 500,
      }),
    } as AssessmentResult);
    const client = renderHero();
    expect(
      await screen.findByRole('heading', {
        name: 'Chrono Trigger',
      }),
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(client.getQueryState(enrichmentWorkspaceKeys.title('chrono'))?.status).toBe('error'),
    );
    expect(
      screen.queryByRole('group', {
        name: 'Enrichment status',
      }),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('Settled')).not.toBeInTheDocument();
  });

  it('adapts to its column through a named container instead of the viewport', async () => {
    const heroStyles = readFileSync(`${import.meta.dirname}/TitleDetail.module.css`, 'utf8');
    renderHero();
    expect(await screen.findByTestId('title-header')).toHaveClass(classes.hero);
    expect(heroStyles).not.toMatch(/@media/);
    expect(heroStyles).toMatch(/\.hero \{ container: title-hero \/ inline-size;/);
    const narrow = heroStyles.slice(
      heroStyles.indexOf('@container title-hero (max-width: 600px) {'),
    );
    for (const selector of [
      '.identity',
      '.poster',
      '.title',
      '.heroShade',
    ])
      expect(narrow).toContain(`${selector} {`);
  });

  it('does not fetch the Manager-only assessment for other roles', async () => {
    permissions.canManageTitles = false;
    renderHero();
    expect(
      await screen.findByRole('heading', {
        name: 'Chrono Trigger',
      }),
    ).toBeInTheDocument();
    expect(getTitleEnrichmentAssessment).not.toHaveBeenCalled();
    expect(
      screen.queryByRole('group', {
        name: 'Enrichment status',
      }),
    ).not.toBeInTheDocument();
  });

  it('uses a fresh RVZ-where-available title ticket instead of the stale GET link', async () => {
    vi.mocked(createDownloadTicket).mockResolvedValue({
      data: {
        downloadUrl: '/api/download-tickets/title-ticket',
        expiresAt: '2026-10-01T22:00:00Z',
      },
    } as never);
    renderHero({
      title: ownedTitle([
        original,
        rvz,
        chd,
      ]),
    });
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Download title ZIP',
      }),
    );
    await user.click(
      screen.getByRole('menuitem', {
        name: 'RVZ where available',
      }),
    );
    await waitFor(() =>
      expect(startBrowserDownload).toHaveBeenCalledWith('/api/download-tickets/title-ticket'),
    );
    expect(createDownloadTicket).toHaveBeenCalledWith({
      body: {
        target: 'Title',
        id: 'chrono',
        format: 'rvz',
      },
    });
    expect(
      screen.queryByRole('link', {
        name: 'Download best available version',
      }),
    ).not.toBeInTheDocument();
  });
  it('offers only the formats the server lists for the title, with its labels', async () => {
    renderHero({
      title: ownedTitle([
        original,
        {
          format: 'rvz',
          label: 'Disc image',
        },
      ]),
    });
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Download title ZIP',
      }),
    );
    expect(screen.getAllByRole('menuitem').map((item) => item.textContent)).toEqual([
      'Original',
      'Disc image where available',
    ]);
  });

  it('downloads a cartridge-only title with one action instead of a format menu', async () => {
    vi.mocked(createDownloadTicket)
      .mockReset()
      .mockResolvedValue({
        data: {
          downloadUrl: '/api/download-tickets/cartridge',
        },
      } as never);
    renderHero({
      title: ownedTitle([
        original,
      ]),
    });
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Download title ZIP',
      }),
    );
    expect(screen.queryByRole('menuitem')).not.toBeInTheDocument();
    await waitFor(() =>
      expect(startBrowserDownload).toHaveBeenCalledWith('/api/download-tickets/cartridge'),
    );
    expect(createDownloadTicket).toHaveBeenCalledExactlyOnceWith({
      body: {
        target: 'Title',
        id: 'chrono',
        format: 'original',
      },
    });
  });

  it('re-authorizes original downloads and never hands off failed title preparations', async () => {
    vi.mocked(startBrowserDownload).mockClear();
    vi.mocked(createDownloadTicket)
      .mockReset()
      .mockResolvedValueOnce({
        error: {
          status: 403,
        },
      } as never)
      .mockResolvedValueOnce({
        data: {
          downloadUrl: '/api/download-tickets/original-fresh',
        },
      } as never);
    renderHero({
      title: ownedTitle([
        original,
        rvz,
        chd,
      ]),
    });
    const user = userEvent.setup();
    const button = await screen.findByRole('button', {
      name: 'Download title ZIP',
    });
    await user.click(button);
    await user.click(
      screen.getByRole('menuitem', {
        name: 'Original',
      }),
    );
    await waitFor(() => expect(createDownloadTicket).toHaveBeenCalledTimes(1));
    expect(startBrowserDownload).not.toHaveBeenCalled();
    await user.click(button);
    await user.click(
      screen.getByRole('menuitem', {
        name: 'Original',
      }),
    );
    await waitFor(() =>
      expect(startBrowserDownload).toHaveBeenCalledWith('/api/download-tickets/original-fresh'),
    );
    expect(createDownloadTicket).toHaveBeenNthCalledWith(2, {
      body: {
        target: 'Title',
        id: 'chrono',
        format: 'original',
      },
    });
  });

  it('authorizes CHD title ZIPs afresh and refuses a failed preparation before retry', async () => {
    vi.mocked(startBrowserDownload).mockClear();
    vi.mocked(createDownloadTicket)
      .mockReset()
      .mockResolvedValueOnce({
        error: {
          status: 403,
        },
      } as never)
      .mockResolvedValueOnce({
        data: {
          downloadUrl: '/api/download-tickets/title-chd-fresh',
        },
      } as never);
    renderHero({
      title: ownedTitle([
        original,
        rvz,
        chd,
      ]),
    });
    const user = userEvent.setup();
    const button = await screen.findByRole('button', {
      name: 'Download title ZIP',
    });
    await user.click(button);
    await user.click(
      screen.getByRole('menuitem', {
        name: 'CHD where available',
      }),
    );
    await waitFor(() => expect(createDownloadTicket).toHaveBeenCalledTimes(1));
    expect(startBrowserDownload).not.toHaveBeenCalled();
    await waitFor(() => expect(button).not.toHaveAttribute('data-loading'));
    await user.click(button);
    await user.click(
      screen.getByRole('menuitem', {
        name: 'CHD where available',
      }),
    );
    await waitFor(() =>
      expect(startBrowserDownload).toHaveBeenCalledExactlyOnceWith(
        '/api/download-tickets/title-chd-fresh',
      ),
    );
    for (const attempt of [
      1,
      2,
    ])
      expect(createDownloadTicket).toHaveBeenNthCalledWith(attempt, {
        body: {
          target: 'Title',
          id: 'chrono',
          format: 'chd',
        },
      });
  });
});
