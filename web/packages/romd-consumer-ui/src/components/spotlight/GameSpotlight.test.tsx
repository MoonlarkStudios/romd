import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ConsumerPresentation } from '../../ConsumerPresentation';
import { GameSpotlight } from './GameSpotlight';

describe('GameSpotlight', () => {
  it('shows the text title and poster beside the copy when there is no hero artwork', () => {
    const { container } = render(
      <ConsumerPresentation>
        <GameSpotlight
          name="Chrono Trigger"
          poster={
            <img
              src="/poster.png"
              alt="Poster"
            />
          }
          metadata={<p>1995 · RPG</p>}
          actions={<button type="button">View game</button>}
        />
      </ConsumerPresentation>,
    );
    expect(
      screen.getByRole('heading', {
        level: 1,
        name: 'Chrono Trigger',
      }),
    ).toHaveTextContent('Chrono Trigger');
    expect(
      screen.getByRole('img', {
        name: 'Poster',
      }),
    ).toBeInTheDocument();
    expect(screen.getByText('1995 · RPG')).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'View game',
      }),
    ).toBeInTheDocument();
    const surface = container.querySelector('.romd-spotlight-surface');
    expect(surface?.firstElementChild).toHaveClass('romd-home-spotlight');
    expect(surface?.firstElementChild).not.toHaveAttribute('data-has-art');
  });

  it('fills the surface with hero artwork, hides the poster, and keeps the name accessible behind a logo', () => {
    const { container } = render(
      <ConsumerPresentation>
        <GameSpotlight
          name="Chrono Trigger"
          art={
            <img
              src="/hero.png"
              alt=""
            />
          }
          poster={
            <img
              src="/poster.png"
              alt="Poster"
            />
          }
          logo={
            <img
              src="/logo.png"
              alt=""
            />
          }
        />
      </ConsumerPresentation>,
    );
    const heading = screen.getByRole('heading', {
      level: 1,
      name: 'Chrono Trigger',
    });
    expect(heading).not.toHaveTextContent('Chrono Trigger');
    expect(heading.querySelector('.romd-home-spotlight-logo')).toHaveAttribute(
      'aria-hidden',
      'true',
    );
    expect(
      screen.queryByRole('img', {
        name: 'Poster',
      }),
    ).not.toBeInTheDocument();
    expect(container.querySelector('.romd-home-spotlight')).toHaveAttribute('data-has-art', 'true');
    expect(container.querySelector('.romd-home-spotlight-art img')).toHaveAttribute(
      'src',
      '/hero.png',
    );
  });
});
