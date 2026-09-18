import type { Dat } from '@romd/admin-api-client';
import { screen } from '@testing-library/react';
import { Route, Routes, useLocation, useParams } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { LegacyExplorerRedirect } from './LegacyExplorerRedirect';

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    listDats: vi.fn(),
    getCurrentUser: vi.fn(),
  };
});

import { getCurrentUser, listDats } from '@romd/admin-api-client';

const mockListDats = listDats as ReturnType<typeof vi.fn>;
const mockGetCurrentUser = getCurrentUser as ReturnType<typeof vi.fn>;

const dats: Dat[] = [
  {
    id: 'dat-1',
    name: 'No-Intro - SNES',
    type: 'No-Intro',
    systemKey: 'plat-1',
    gameCount: '500',
    romCount: '600',
    importedAt: '2024-01-15T08:00:00Z',
  },
];

function HubProbe() {
  const { systemKey } = useParams();
  const location = useLocation();
  return (
    <div>
      hub:{systemKey}
      {location.search}
    </div>
  );
}

function renderRedirect(initialEntry: string) {
  return render(
    <Routes>
      <Route path="/registry/explorer" element={<LegacyExplorerRedirect />} />
      <Route path="/systems/:systemKey" element={<HubProbe />} />
      <Route path="/systems" element={<div>index probe</div>} />
    </Routes>,
    { routerOptions: { initialEntries: [initialEntry] } },
  );
}

describe('LegacyExplorerRedirect', () => {
  beforeEach(() => {
    mockListDats.mockResolvedValue({ data: dats, error: undefined });
    mockGetCurrentUser.mockResolvedValue({ data: null, error: { status: 401 } });
  });

  afterEach(() => {
    vi.resetAllMocks();
  });

  it('maps a system selection to its hub', async () => {
    renderRedirect('/registry/explorer?type=system&id=plat-9');

    expect(await screen.findByText('hub:plat-9')).toBeInTheDocument();
  });

  it('maps a DAT selection to its system hub with the DAT preselected', async () => {
    renderRedirect('/registry/explorer?type=dat&id=dat-1');

    expect(await screen.findByText('hub:plat-1?tab=sources&dat=dat-1')).toBeInTheDocument();
  });

  it('falls back to the Systems index without a selection', async () => {
    renderRedirect('/registry/explorer');

    expect(await screen.findByText('index probe')).toBeInTheDocument();
  });
});
