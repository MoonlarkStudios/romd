import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { GameDetailHero } from '../src/components/title-detail/GameDetailHero';

describe('GameDetailHero', () => {
  it('keeps the title accessible and restores text when a logo fails', () => {
    render(<GameDetailHero name="Example game" logoUrl="/logo.png" backdrop={<img src="/scene.png" alt="Backdrop" />} facts={['1994', null, 'Adventure']} />);
    expect(screen.getByRole('heading', { name: 'Example game', level: 1 })).toBeInTheDocument();
    expect(screen.getByText('1994 · Adventure')).toBeInTheDocument();
    fireEvent.error(screen.getByRole('heading').querySelector('img')!);
    expect(screen.getByRole('heading')).toHaveTextContent('Example game');
  });

  it('keeps host actions and poster available for responsive layouts with or without a backdrop', () => {
    const props = { name: 'Example', poster: <img src="/poster.png" alt="Poster" />, actions: <button type="button">Host action</button> };
    const { rerender } = render(<GameDetailHero {...props} />);
    expect(screen.getByRole('img', { name: 'Poster' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Host action' })).toBeInTheDocument();
    rerender(<GameDetailHero {...props} backdrop={<img src="/scene.png" alt="Backdrop" />} />);
    expect(screen.getByRole('img', { name: 'Poster' })).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Example overview' })).toHaveAttribute('data-has-backdrop', 'true');
  });
});
