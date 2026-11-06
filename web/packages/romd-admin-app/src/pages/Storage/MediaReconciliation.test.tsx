import {
  client,
  type MediaReconciliationPolicyDto,
  type StartMediaReconciliationSelectionRequest,
} from '@romd/admin-api-client';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { Route, Routes } from 'react-router';
import { afterEach, beforeEach, expect, it } from 'vitest';
import { server } from '../../test/msw/server';
import { render } from '../../test/utils/render';
import { MediaReconciliation } from './MediaReconciliation';

const policies: MediaReconciliationPolicyDto[] = [
  {
    kindId: 'gamecube-disc',
    recipeId: 'rvz/v1',
    conversionMode: 'Automatic',
    systemKeys: [
      'gc',
    ],
    manifestType: 'WholeFile',
    storedFormatLabel: 'RVZ',
    canDecode: true,
    qualifiedRuntime: null,
  },
  {
    kindId: 'ps1-cd',
    recipeId: 'chd-cd/v1',
    conversionMode: 'OnRequest',
    systemKeys: [
      'psx',
    ],
    manifestType: 'CdTracks',
    storedFormatLabel: 'CHD',
    canDecode: false,
    qualifiedRuntime: 'linux-arm64',
  },
];
const jobId = '11111111-2222-4333-8444-555555555555';
beforeEach(() => {
  client.setConfig({
    baseUrl: 'http://localhost',
  });
  server.use(
    http.get('http://localhost/api/media/reconciliation/policies', () =>
      HttpResponse.json(policies),
    ),
  );
});
afterEach(() =>
  client.setConfig({
    baseUrl: '',
  }),
);
function show() {
  render(
    <Routes>
      <Route
        path="/storage"
        element={<MediaReconciliation />}
      />
      <Route
        path="/jobs/:jobId"
        element={<div>Following accepted media job</div>}
      />
    </Routes>,
    {
      routerOptions: {
        initialEntries: [
          '/storage',
        ],
      },
    },
  );
}
async function loaded() {
  show();
  await screen.findByRole('table', {
    name: 'Current media policies',
  });
}
async function choose(label: string) {
  await userEvent.click(
    screen.getByRole('combobox', {
      name: 'Selection',
    }),
  );
  await userEvent.click(
    screen.getByRole('option', {
      name: label,
    }),
  );
}
it('presents current policy and captures a finite selection with an explicit conversion choice', async () => {
  const bodies: StartMediaReconciliationSelectionRequest[] = [];
  server.use(
    http.post('http://localhost/api/media/reconciliation/selections', async ({ request }) => {
      bodies.push((await request.json()) as StartMediaReconciliationSelectionRequest);
      return HttpResponse.json(
        {
          jobId,
        },
        {
          status: 202,
        },
      );
    }),
  );
  await loaded();
  expect(screen.getByText('On request')).toBeInTheDocument();
  expect(screen.getByText('Unavailable')).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Start reconciliation',
    }),
  );
  await screen.findByText('Following accepted media job');
  expect(bodies).toEqual([
    {
      requestId: expect.any(String),
      filter: 'AllSubjects',
      kindId: null,
      systemKey: null,
      limit: 100,
      conversionRequested: false,
    },
  ]);
});
it('always requests verification without conversion for retained representations', async () => {
  const bodies: unknown[] = [];
  server.use(
    http.post('http://localhost/api/media/reconciliation/selections', async ({ request }) => {
      bodies.push(await request.json());
      return HttpResponse.json(
        {
          jobId,
        },
        {
          status: 202,
        },
      );
    }),
  );
  await loaded();
  await userEvent.click(
    screen.getByRole('checkbox', {
      name: 'Request conversion for kinds that require it',
    }),
  );
  await choose('Verify retained representations');
  expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Start reconciliation',
    }),
  );
  await screen.findByText('Following accepted media job');
  expect(bodies).toEqual([
    {
      requestId: expect.any(String),
      filter: 'RetainedRepresentations',
      kindId: null,
      systemKey: null,
      limit: 100,
      conversionRequested: false,
    },
  ]);
});
it('retains request identity after a failed response and changes it only when the scope changes', async () => {
  const bodies: StartMediaReconciliationSelectionRequest[] = [];
  server.use(
    http.post('http://localhost/api/media/reconciliation/selections', async ({ request }) => {
      bodies.push((await request.json()) as StartMediaReconciliationSelectionRequest);
      return HttpResponse.json(
        {
          detail: 'Selection unavailable.',
        },
        {
          status: 503,
        },
      );
    }),
  );
  await loaded();
  const start = screen.getByRole('button', {
    name: 'Start reconciliation',
  });
  await userEvent.click(start);
  await screen.findByText('Selection unavailable.');
  expect(bodies).toHaveLength(1);
  await userEvent.click(start);
  await waitFor(() => expect(bodies).toHaveLength(2));
  expect(bodies[1]).toEqual(bodies[0]);
  fireEvent.change(
    screen.getByRole('textbox', {
      name: 'Maximum subjects',
    }),
    {
      target: {
        value: '17',
      },
    },
  );
  await userEvent.click(start);
  await waitFor(() => expect(bodies).toHaveLength(3));
  expect(bodies[2].limit).toBe(17);
  expect(bodies[2].requestId).not.toBe(bodies[0].requestId);
});
it('requires a registered kind when a kind selection is chosen', async () => {
  await loaded();
  await choose('One media kind');
  expect(
    screen.getByRole('button', {
      name: 'Start reconciliation',
    }),
  ).toBeDisabled();
  await userEvent.click(
    screen.getByRole('combobox', {
      name: 'Media kind',
    }),
  );
  await userEvent.click(
    screen.getByRole('option', {
      name: 'ps1 cd',
    }),
  );
  expect(
    screen.getByRole('button', {
      name: 'Start reconciliation',
    }),
  ).toBeEnabled();
});
it('guards repeated submissions while an accepted response is pending', async () => {
  let calls = 0;
  let release: (() => void) | undefined;
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  server.use(
    http.post('http://localhost/api/media/reconciliation/selections', async () => {
      calls++;
      await held;
      return HttpResponse.json(
        {
          jobId,
        },
        {
          status: 202,
        },
      );
    }),
  );
  await loaded();
  try {
    const button = screen.getByRole('button', {
      name: 'Start reconciliation',
    });
    fireEvent.click(button);
    fireEvent.click(button);
    await waitFor(() => expect(calls).toBe(1));
  } finally {
    release?.();
  }
  await screen.findByText('Following accepted media job');
  expect(calls).toBe(1);
});
