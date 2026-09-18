import { MantineProvider } from '@mantine/core';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { BrowserRouter } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { AuthProvider } from '../../contexts/AuthContext';
import { Header } from './Header';

vi.mock('../../api/client', () => ({
  getAuthToken: () => null,
}));

vi.mock('@romd/admin-api-client', () => ({
  getCurrentUser: vi.fn().mockResolvedValue({ data: null }),
  login: vi.fn(),
}));

// The Activity center has its own provider needs (jobs query, socket); the
// Header test only cares about the header itself.
vi.mock('../Activity/ActivityCenter', () => ({
  ActivityCenter: () => null,
}));

function renderHeader(opened = false, toggle = vi.fn()) {
  return render(
    <MantineProvider>
      <BrowserRouter>
        <AuthProvider>
          <Header
            opened={opened}
            toggle={toggle}
          />
        </AuthProvider>
      </BrowserRouter>
    </MantineProvider>,
  );
}

describe('Header', () => {
  it('renders the title', () => {
    renderHeader();
    expect(screen.getByText('ROMD Admin')).toBeInTheDocument();
  });

  it('renders the theme toggle button', () => {
    renderHeader();
    expect(
      screen.getByRole('button', {
        name: /toggle color scheme/i,
      }),
    ).toBeInTheDocument();
  });

  it('calls toggle when burger is clicked on mobile', async () => {
    const toggle = vi.fn();
    renderHeader(false, toggle);

    const burger = screen.getByRole('button', {
      name: /toggle navigation/i,
    });
    await userEvent.click(burger);

    expect(toggle).toHaveBeenCalledOnce();
  });
});
