import type { ConsumerTitleCardDto } from '@romd/consumer-api-client';
import { act, fireEvent, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { TitleRail } from './TitleRail';

vi.mock('./PosterCard', () => ({ PosterCard: ({ title }: { title: ConsumerTitleCardDto }) => <div>{title.name}</div> }));
const titles = Array.from({ length: 8 }, (_, i) => ({ id: String(i), name: `Game ${i}` })) as ConsumerTitleCardDto[];
afterEach(() => vi.unstubAllGlobals());

function setup() {
  let resize = () => {};
  vi.stubGlobal('ResizeObserver', class {
    constructor(callback: () => void) { resize = callback; }
    observe() {}
    disconnect() {}
  });
  const { container } = render(<TitleRail title="Games" titles={titles} />, { withAuth: false });
  const rail = container.querySelector('.romd-rail') as HTMLElement;
  Object.defineProperties(rail, { clientWidth: { configurable: true, value: 650 }, scrollWidth: { configurable: true, value: 1760 } });
  for (const [index, child] of [...rail.children].entries()) Object.defineProperty(child, 'offsetLeft', { value: 4 + index * 220 });
  rail.scrollTo = vi.fn();
  act(() => resize());
  return { rail, resize };
}

describe('TitleRail', () => {
  it('tracks scroll edges and removes both controls when every card fits after resize', () => {
    const { rail, resize } = setup();
    expect(screen.getByRole('button', { name: 'Scroll left' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Scroll right' })).toBeEnabled();
    expect(rail).not.toHaveAttribute('data-left');
    expect(rail).toHaveAttribute('data-right');
    rail.scrollLeft = 1110;
    fireEvent.scroll(rail);
    expect(screen.getByRole('button', { name: 'Scroll left' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Scroll right' })).toBeDisabled();
    Object.defineProperty(rail, 'clientWidth', { value: 1760 });
    rail.scrollLeft = 0;
    act(() => resize());
    expect(rail).not.toHaveAttribute('data-left');
    expect(rail).not.toHaveAttribute('data-right');
    expect(screen.getByRole('button', { name: 'Scroll right' })).toBeDisabled();
  });

  it('pages by whole cards and clamps at the end', () => {
    const { rail } = setup();
    fireEvent.click(screen.getByRole('button', { name: 'Scroll right' }));
    expect(rail.scrollTo).toHaveBeenLastCalledWith({ left: 440, behavior: 'smooth' });
    rail.scrollLeft = 1000;
    fireEvent.scroll(rail);
    fireEvent.click(screen.getByRole('button', { name: 'Scroll right' }));
    expect(rail.scrollTo).toHaveBeenLastCalledWith({ left: 1110, behavior: 'smooth' });
  });

  it('uses immediate paging when reduced motion is requested', () => {
    const { rail } = setup();
    vi.stubGlobal('matchMedia', () => ({ matches: true }));
    fireEvent.click(screen.getByRole('button', { name: 'Scroll right' }));
    expect(rail.scrollTo).toHaveBeenLastCalledWith({ left: 440, behavior: 'instant' });
  });
});
