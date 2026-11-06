import { MantineProvider } from '@mantine/core';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { CatalogPresentationPreview } from './CatalogPresentationPreview';

vi.mock('../../../hooks/api/usePlatforms', () => ({
  usePlatform: () => ({
    data: {
      name: 'Super Nintendo',
    },
  }),
}));

afterEach(() => {
  sessionStorage.clear();
  vi.restoreAllMocks();
});

describe('CatalogPresentationPreview', () => {
  it('shows consumer game details at the chosen device width', () => {
    vi.spyOn(Element.prototype, 'clientWidth', 'get').mockReturnValue(380);
    render(
      <MantineProvider env="test">
        <CatalogPresentationPreview
          title={{
            name: 'Chrono Trigger',
            systemKey: 'snes',
            genre: 'RPG',
            artwork: [],
          }}
        />
      </MantineProvider>,
    );
    const mobile = screen.getByRole('figure', {
      name: 'Mobile preview, 390 pixels wide',
    });
    expect(
      within(mobile).getByRole('region', {
        hidden: true,
        name: 'Chrono Trigger overview',
      }),
    ).toBeInTheDocument();
    expect(within(mobile).getByText('RPG')).toBeInTheDocument();
    fireEvent.click(
      screen.getByRole('radio', {
        name: 'Desktop',
      }),
    );
    expect(
      screen.getByRole('figure', {
        name: 'Desktop preview, 1440 pixels wide',
      }),
    ).toBeInTheDocument();
  });
});
