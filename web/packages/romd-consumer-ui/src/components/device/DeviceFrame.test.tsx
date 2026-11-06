import { act, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DeviceFrame, deviceScale } from './DeviceFrame';

let resize: () => void = () => undefined;

function layout({ available, contentHeight }: { available: number; contentHeight: number }) {
  vi.spyOn(Element.prototype, 'clientWidth', 'get').mockReturnValue(available);
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(contentHeight);
}

function screenOf(name: string) {
  const frame = screen.getByRole('figure', {
    name,
  });
  return {
    frame,
    inner: frame.firstElementChild as HTMLElement,
  };
}

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('deviceScale', () => {
  it('scales down to the available width and never up', () => {
    expect(deviceScale(720, 1440)).toBe(0.5);
    expect(deviceScale(195, 390)).toBe(0.5);
    expect(deviceScale(390, 390)).toBe(1);
    expect(deviceScale(2000, 1440)).toBe(1);
  });

  it('renders unmeasured space at full size instead of collapsing', () => {
    expect(deviceScale(0, 834)).toBe(1);
  });
});

describe('DeviceFrame', () => {
  it('renders children at the device width inside an inline-size container', () => {
    render(
      <DeviceFrame device="tablet">
        <p>Consumer surface</p>
      </DeviceFrame>,
    );
    const { inner } = screenOf('Tablet preview, 834 pixels wide');
    expect(inner).toContainElement(screen.getByText('Consumer surface'));
    expect(inner.style.width).toBe('834px');
    expect(inner.style.containerType).toBe('inline-size');
    expect(inner.style.transformOrigin).toBe('top left');
  });

  it('keeps the rendered surface inert so only the figure label is announced', () => {
    render(
      <DeviceFrame device="mobile">
        <button type="button">Consumer action</button>
      </DeviceFrame>,
    );
    const { frame, inner } = screenOf('Mobile preview, 390 pixels wide');
    expect(frame).toHaveAccessibleName('Mobile preview, 390 pixels wide');
    expect(inner).toHaveAttribute('inert');
    expect(inner).toContainElement(screen.getByText('Consumer action'));
  });

  it('gives consumer surfaces the device viewport height instead of the host window', () => {
    render(
      <DeviceFrame device="desktop">
        <p>Detail hero</p>
      </DeviceFrame>,
    );
    expect(
      screenOf('Desktop preview, 1440 pixels wide').inner.style.getPropertyValue(
        '--romd-viewport-height',
      ),
    ).toBe('900px');
  });

  it('scales to the available width, reserves the scaled height, and follows resizes', () => {
    vi.stubGlobal(
      'ResizeObserver',
      class {
        constructor(callback: () => void) {
          resize = callback;
        }
        observe() {}
        disconnect() {}
      },
    );
    layout({
      available: 720,
      contentHeight: 601,
    });
    render(
      <DeviceFrame device="desktop">
        <p>Spotlight</p>
      </DeviceFrame>,
    );
    const { frame, inner } = screenOf('Desktop preview, 1440 pixels wide');
    expect(inner.style.transform).toBe('scale(0.5)');
    expect(frame.style.height).toBe('301px');
    layout({
      available: 2880,
      contentHeight: 601,
    });
    act(() => resize());
    expect(inner.style.transform).toBe('scale(1)');
    expect(frame.style.height).toBe('601px');
  });

  it('clips hero surfaces to the device viewport height', () => {
    layout({
      available: 195,
      contentHeight: 2000,
    });
    render(
      <DeviceFrame
        device="mobile"
        fixedHeight
      >
        <p>Detail hero</p>
      </DeviceFrame>,
    );
    const { frame, inner } = screenOf('Mobile preview, 390 pixels wide');
    expect(inner.style.height).toBe('844px');
    expect(inner.style.overflow).toBe('hidden');
    expect(inner.style.transform).toBe('scale(0.5)');
    expect(frame.style.height).toBe('422px');
  });
});
