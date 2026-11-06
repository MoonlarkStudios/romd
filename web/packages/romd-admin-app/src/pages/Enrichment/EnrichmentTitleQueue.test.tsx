import { MantineProvider } from '@mantine/core';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import { EnrichmentTitleQueue } from './EnrichmentTitleQueue';

it('uses compact platform text with its full name, and recovers from a failed thumbnail on URL change', () => {
  const content = (thumbnail: string) => (
    <MantineProvider env="test">
      <EnrichmentTitleQueue
        items={[
          {
            id: 'turok',
            name: 'Turok',
            platform: 'N64',
            platformTitle: 'Nintendo 64',
            thumbnail,
            reviewing: true,
            flags: {
              needsReview: true,
              hasOperationalBlocker: false,
              hasActiveWork: true,
              canEnrichAutomatically: false,
            },
            outstandingCount: 1,
          },
        ]}
        selectedId="turok"
        pending={false}
        hasMore={false}
        loadingMore={false}
        refreshing={false}
        onSelect={vi.fn()}
        onMore={vi.fn()}
      />
    </MantineProvider>
  );
  const { container, rerender } = render(content('/artwork/one.jpg'));
  expect(screen.getByText('1 title')).toBeInTheDocument();
  expect(screen.getByText('N64')).toHaveAttribute('aria-label', 'Nintendo 64');
  expect(screen.getByText('1 outstanding')).toBeInTheDocument();
  expect(
    within(
      screen.getByRole('group', {
        name: 'Turok enrichment status',
      }),
    )
      .getAllByText(/./)
      .map((chip) => chip.textContent),
  ).toEqual([
    'Needs review',
    'Processing',
  ]);
  const image = container.querySelector('img');
  expect(image).toHaveAttribute('src', '/artwork/one.jpg');
  if (!image) throw new Error('Missing enrichment image');
  fireEvent.error(image);
  expect(container.querySelector('img')).toBeNull();
  expect(screen.getByText('T')).toBeInTheDocument();
  rerender(content('/artwork/two.jpg'));
  expect(container.querySelector('img')).toHaveAttribute('src', '/artwork/two.jpg');
  expect(screen.queryByText('T')).not.toBeInTheDocument();
});
