import { MantineProvider } from '@mantine/core';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TitleDetailView as TitleDetail } from '../../hooks/api/useTitleDetail';
import { writtenTitleViewFixture } from '../../test/msw/fixtures/enrichment';
import { ArtworkComposer, type ArtworkDraft } from './ArtworkComposer';

vi.mock('../../hooks/api/usePlatforms', () => ({
  usePlatform: () => ({
    data: {
      name: 'Super Nintendo',
    },
  }),
}));

class TestPointerEvent extends MouseEvent {
  readonly pointerId: number;
  constructor(type: string, init: PointerEventInit = {}) {
    super(type, init);
    this.pointerId = init.pointerId ?? 1;
  }
}

const title: TitleDetail = writtenTitleViewFixture({
  id: 'chrono',
  systemKey: 'snes',
  name: 'Chrono Trigger',
  enrichmentStatus: 'None',
  artwork: [],
});
const draft: ArtworkDraft = {
  role: 'Hero',
  url: '/draft/hero.png',
  revision: '0',
  focalX: 50,
  focalY: 50,
};

function show(role: ArtworkDraft['role'] = 'Hero') {
  const onChange = vi.fn();
  render(
    <MantineProvider env="test">
      <ArtworkComposer
        title={title}
        role={role}
        draft={{
          ...draft,
          role,
        }}
        busy={false}
        error={null}
        onChange={onChange}
        onCancel={vi.fn()}
        onSave={vi.fn()}
      />
    </MantineProvider>,
  );
  const focal = screen.getByRole('slider', {
    name: 'Focal point',
  });
  vi.spyOn(focal, 'getBoundingClientRect').mockReturnValue({
    left: 0,
    top: 0,
    width: 200,
    height: 100,
  } as DOMRect);
  return {
    onChange,
    focal,
  };
}

const heroImage = () =>
  document.querySelector<HTMLImageElement>('.romd-home-spotlight-art img[src="/draft/hero.png"]');

beforeEach(() => {
  vi.stubGlobal('PointerEvent', TestPointerEvent);
  Object.defineProperty(Element.prototype, 'setPointerCapture', {
    configurable: true,
    value: vi.fn(),
  });
});

afterEach(() => {
  Reflect.deleteProperty(Element.prototype, 'setPointerCapture');
  sessionStorage.clear();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('ArtworkComposer focal point', () => {
  it('previews a drag live and updates the draft once when it is released', () => {
    const { onChange, focal } = show();
    expect(focal.closest('figure, [inert]')).toBeNull();
    fireEvent.pointerDown(focal, {
      button: 0,
      pointerId: 3,
      clientX: 20,
      clientY: 20,
    });
    fireEvent.pointerMove(focal, {
      pointerId: 3,
      clientX: 40,
      clientY: 50,
    });
    fireEvent.pointerMove(focal, {
      pointerId: 3,
      clientX: 50,
      clientY: 75,
    });
    expect(onChange).not.toHaveBeenCalled();
    expect(heroImage()?.style.objectPosition).toBe('25% 75%');
    expect(
      screen.getByRole('slider', {
        name: 'Horizontal focal point',
      }),
    ).toHaveAttribute('aria-valuenow', '25');
    fireEvent.pointerUp(focal, {
      pointerId: 3,
    });
    fireEvent.lostPointerCapture(focal, {
      pointerId: 3,
    });
    expect(onChange).toHaveBeenCalledOnce();
    expect(onChange).toHaveBeenCalledWith({
      ...draft,
      focalX: 25,
      focalY: 75,
    });
  });

  it('moves by keyboard and updates the draft once per key release', () => {
    const { onChange, focal } = show('Backdrop');
    fireEvent.keyDown(focal, {
      key: 'ArrowRight',
    });
    fireEvent.keyDown(focal, {
      key: 'ArrowRight',
    });
    fireEvent.keyDown(focal, {
      key: 'ArrowUp',
      shiftKey: true,
    });
    expect(onChange).not.toHaveBeenCalled();
    fireEvent.keyUp(focal, {
      key: 'ArrowUp',
    });
    expect(onChange).toHaveBeenCalledOnce();
    expect(onChange).toHaveBeenCalledWith({
      ...draft,
      role: 'Backdrop',
      focalX: 52,
      focalY: 40,
    });
  });
});

describe('ArtworkComposer preview', () => {
  it('shows a banner on the consumer spotlight and switches device from a width-derived default', () => {
    vi.spyOn(Element.prototype, 'clientWidth', 'get').mockReturnValue(600);
    show();
    expect(
      screen.getByRole('radio', {
        name: 'Tablet',
      }),
    ).toBeChecked();
    const tablet = screen.getByRole('figure', {
      name: 'Tablet preview, 834 pixels wide',
    });
    expect(
      within(tablet).getByRole('heading', {
        hidden: true,
        level: 1,
        name: 'Chrono Trigger',
      }),
    ).toBeInTheDocument();
    expect(within(tablet).getByText('View game')).toBeInTheDocument();
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
    expect(sessionStorage.getItem('romd:artwork-preview-device')).toBe('desktop');
  });
});
