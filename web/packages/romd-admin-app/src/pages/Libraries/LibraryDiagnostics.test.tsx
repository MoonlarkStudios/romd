import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { LibraryDiagnostics } from './LibraryDiagnostics';

vi.mock('../../components/TitleSearchSelect', () => ({
  TitleSearchSelect: () => null,
}));
vi.mock('../../hooks/api/useLibraryManagement', () => ({
  useLibraryTitleReleaseDiagnostics: () => ({
    data: [
      {
        id: 'release',
        name: 'Multi-disc game',
        isExposed: true,
        exposureReason: 'ExposedDefault',
        vfsPathError: 'Original paths conflict on case-insensitive filesystems.',
      },
    ],
    isPending: false,
    isError: false,
    isSuccess: true,
  }),
}));

it('shows why a visible release cannot appear in VFS', async () => {
  render(<LibraryDiagnostics libraryId="library" />);
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Missing a game? Check its visibility',
    }),
  );
  await waitFor(() => expect(screen.getByText('Visible')).toBeVisible());
  expect(
    screen.getByText('VFS unavailable: Original paths conflict on case-insensitive filesystems.'),
  ).toBeVisible();
});
