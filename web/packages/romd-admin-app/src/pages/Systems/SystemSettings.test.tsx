import { listManagedSystems, setSystemEnabled } from '@romd/admin-api-client';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { SystemSettings } from './SystemSettings';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  listManagedSystems: vi.fn(),
  setSystemEnabled: vi.fn(),
}));
it('requires confirmation to remove the system and preserves data in its explanation', async () => {
  vi.mocked(listManagedSystems).mockResolvedValue({
    data: [
      {
        key: 'one',
        name: 'PlayStation',
        enabled: true,
      },
    ],
  } as never);
  vi.mocked(setSystemEnabled).mockResolvedValue({} as never);
  const user = userEvent.setup();
  render(<SystemSettings systemKey="one" />);
  await user.click(
    await screen.findByRole('button', {
      name: 'Remove from Your systems',
    }),
  );
  const dialog = await screen.findByRole('dialog');
  expect(within(dialog).getByText(/does not delete anything/)).toBeInTheDocument();
  expect(setSystemEnabled).not.toHaveBeenCalled();
  await user.click(
    within(dialog).getByRole('button', {
      name: 'Remove system',
    }),
  );
  await waitFor(() =>
    expect(setSystemEnabled).toHaveBeenCalledWith({
      path: {
        systemKey: 'one',
      },
      body: {
        enabled: false,
      },
    }),
  );
});
