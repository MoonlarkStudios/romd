import { MantineProvider } from '@mantine/core';
import type { CatalogTitle } from '@romd/admin-api-client';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, it } from 'vitest';
import { CatalogEnrichmentStatus } from './CatalogEnrichmentStatus';

const title: CatalogTitle = {
  id: 'turok',
  name: 'Turok',
  systemKey: 'n64',
  enrichmentStatus: 'Completed',
};
const settled = {
  needsReview: false,
  canEnrichAutomatically: false,
  hasActiveWork: false,
  hasOperationalBlocker: false,
};
const renderStatus = (value: CatalogTitle) =>
  render(
    <MantineProvider>
      <div data-testid="status">
        <CatalogEnrichmentStatus title={value} />
      </div>
    </MantineProvider>,
  );

it('stacks every current flag in the fixed chip order', () => {
  renderStatus({
    ...title,
    enrichmentAssessment: {
      needsReview: true,
      canEnrichAutomatically: true,
      hasActiveWork: true,
      hasOperationalBlocker: true,
    },
  });
  expect(
    screen
      .getAllByText(/^(Needs review|Blocked|Processing|Automatic|Settled)$/)
      .map((chip) => chip.textContent),
  ).toEqual([
    'Needs review',
    'Blocked',
    'Processing',
    'Automatic',
  ]);
});

it('shows current review and processing even after a successful historical attempt', () => {
  renderStatus({
    ...title,
    enrichmentAssessment: {
      ...settled,
      needsReview: true,
      hasActiveWork: true,
    },
  });
  expect(
    screen.getAllByText(/^(Needs review|Processing|Settled)$/).map((chip) => chip.textContent),
  ).toEqual([
    'Needs review',
    'Processing',
  ]);
});

it('shows Settled from the canonical assessment even if the last attempt failed', async () => {
  renderStatus({
    ...title,
    enrichmentStatus: 'Failed',
    enrichmentAssessment: settled,
  });
  expect(screen.getByText('Settled')).toBeInTheDocument();
  expect(screen.queryByText('Resolved')).not.toBeInTheDocument();
  await userEvent.hover(screen.getByText('Settled'));
  expect(await screen.findByText('Last attempt: Failed')).toBeInTheDocument();
});

it('renders nothing without an assessment rather than implying a status', () => {
  renderStatus(title);
  expect(screen.getByTestId('status')).toBeEmptyDOMElement();
  expect(screen.queryByText('Settled')).not.toBeInTheDocument();
  expect(screen.queryByText('Not assessed')).not.toBeInTheDocument();
});
