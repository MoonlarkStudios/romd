import type { Dat, SourceRemovalImpact } from '@romd/admin-api-client';
import { applySourceLifecycle, previewSourceLifecycle } from '@romd/admin-api-client';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { ConfirmSourceStatusModal } from './ConfirmSourceStatusModal';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  applySourceLifecycle: vi.fn(),
  previewSourceLifecycle: vi.fn(),
}));
const dat = {
  id: 'dat',
  name: 'My source',
  sourceId: 'source',
  catalogSourceId: 'catalog',
  lifecycle: 'Active',
  sourceStatus: 'Active',
  description: '',
  type: 'Redump',
} as Dat;
const impact: SourceRemovalImpact = {
  name: dat.name,
  action: 'Delete',
  reviewToken: 'review-1',
  versions: 2,
  stillCovered: 3,
  ownedWithoutDefinition: 4,
  personalWithoutDefinition: 1,
  catalogOnlyWithoutDefinition: 9,
  busy: false,
};
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(previewSourceLifecycle).mockResolvedValue({
    data: impact,
  } as Awaited<ReturnType<typeof previewSourceLifecycle>>);
  vi.mocked(applySourceLifecycle).mockResolvedValue({
    data: undefined,
  } as Awaited<ReturnType<typeof applySourceLifecycle>>);
});
it('shows actual impact and requires the source name before permanent deletion', async () => {
  const user = userEvent.setup();
  const close = vi.fn();
  render(
    <ConfirmSourceStatusModal
      dat={dat}
      targetStatus="Delete"
      onClose={close}
    />,
  );
  await screen.findByText('Titles still covered by another source');
  const confirm = screen.getByRole('button', {
    name: 'Delete source permanently',
  });
  expect(confirm).toBeDisabled();
  expect(screen.getByText(/Stored ROM files and saves are not deleted/)).toBeInTheDocument();
  await user.type(screen.getByRole('textbox'), dat.name);
  await user.click(confirm);
  await waitFor(() =>
    expect(applySourceLifecycle).toHaveBeenCalledWith({
      path: {
        datId: 'dat',
      },
      body: {
        action: 'Delete',
        reviewToken: 'review-1',
      },
    }),
  );
  await waitFor(() => expect(close).toHaveBeenCalled());
});
it('holds the dialog open and refreshes impact after a stale review', async () => {
  const user = userEvent.setup();
  const close = vi.fn();
  vi.mocked(applySourceLifecycle).mockResolvedValue({
    error: {
      status: 409,
    },
  } as Awaited<ReturnType<typeof applySourceLifecycle>>);
  render(
    <ConfirmSourceStatusModal
      dat={dat}
      targetStatus="Disabled"
      onClose={close}
    />,
  );
  await screen.findByText('Titles still covered by another source');
  await user.click(
    screen.getByRole('button', {
      name: 'Disable source',
    }),
  );
  expect(await screen.findByText(/Review the refreshed impact/)).toBeInTheDocument();
  expect(close).not.toHaveBeenCalled();
  await waitFor(() => expect(previewSourceLifecycle).toHaveBeenCalledTimes(2));
});
it('prevents mutation while a source job is running', async () => {
  vi.mocked(previewSourceLifecycle).mockResolvedValue({
    data: {
      ...impact,
      busy: true,
    },
  } as Awaited<ReturnType<typeof previewSourceLifecycle>>);
  render(
    <ConfirmSourceStatusModal
      dat={dat}
      targetStatus="Disabled"
      onClose={vi.fn()}
    />,
  );
  await screen.findByText(/A source check or import is running/);
  expect(
    screen.getByRole('button', {
      name: 'Disable source',
    }),
  ).toBeDisabled();
  expect(applySourceLifecycle).not.toHaveBeenCalled();
});
