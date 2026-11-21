import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { LibraryDiagnostics } from './LibraryDiagnostics';

vi.mock('../../components/TitleSearchSelect', () => ({
  TitleSearchSelect: () => null,
}));
vi.mock('../../hooks/api/useLibraryManagement', () => ({
  useLibraryTitleVisibility: (_library: string, title: string | null) => ({
    data: title
      ? {
          titleId: title,
          titleName: 'Game to review',
          hasPublication: true,
          isPublished: true,
          isEligible: false,
          isOwned: false,
          needsPublish: true,
          reason: 'ContentRating',
          ratingBoard: 'Esrb',
          ratingCategory: 'T',
          ratingAge: 13,
        }
      : undefined,
  }),
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
      name: 'Game visibility',
    }),
  );
  await userEvent.click(screen.getByText('Published release rules (1)'));
  await waitFor(() => expect(screen.getByText('Allowed')).toBeVisible());
  expect(
    screen.getByText('VFS unavailable: Original paths conflict on case-insensitive filesystems.'),
  ).toBeVisible();
});

it('opens on the linked game and distinguishes saved eligibility from the active audience', async () => {
  render(<LibraryDiagnostics libraryId="library" />, {
    routerOptions: {
      initialEntries: [
        '/libraries/library?tab=preview&inspect=title',
      ],
    },
  });
  expect(await screen.findByText('Game to review')).toBeVisible();
  expect(screen.getByText('Exceeds the content-rating ceiling')).toBeVisible();
  expect(screen.getByText('Visible to this audience')).toBeVisible();
  expect(screen.getByText('Publish changes to update what this audience can see.')).toBeVisible();
  expect(
    screen.getByRole('link', {
      name: 'Open ratings',
    }),
  ).toHaveAttribute('href', '/titles/title/ratings');
});
