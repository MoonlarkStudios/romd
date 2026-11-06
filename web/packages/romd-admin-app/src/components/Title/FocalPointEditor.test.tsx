import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { type FocalPoint, FocalPointEditor } from './FocalPointEditor';

class TestPointerEvent extends MouseEvent {
  readonly pointerId: number;
  constructor(type: string, init: PointerEventInit = {}) {
    super(type, init);
    this.pointerId = init.pointerId ?? 1;
  }
}

const capture = vi.fn();

function mount(
  value: FocalPoint = {
    focalX: 50,
    focalY: 50,
  },
  disabled = false,
) {
  const onChange = vi.fn();
  const onCommit = vi.fn();
  render(
    <FocalPointEditor
      src="/hero.png"
      value={value}
      onChange={onChange}
      onCommit={onCommit}
      disabled={disabled}
    />,
  );
  return {
    slider: screen.getByRole('slider', {
      name: 'Focal point',
    }),
    onChange,
    onCommit,
  };
}

beforeEach(() => {
  vi.stubGlobal('PointerEvent', TestPointerEvent);
  Object.defineProperty(Element.prototype, 'setPointerCapture', {
    configurable: true,
    value: capture,
  });
  vi.spyOn(Element.prototype, 'getBoundingClientRect').mockReturnValue({
    left: 100,
    top: 50,
    width: 200,
    height: 100,
  } as DOMRect);
});

afterEach(() => {
  Reflect.deleteProperty(Element.prototype, 'setPointerCapture');
  capture.mockReset();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('FocalPointEditor', () => {
  it('follows a captured drag, clamps to the image, and commits once on release', () => {
    const { slider, onChange, onCommit } = mount();
    fireEvent.pointerDown(slider, {
      pointerId: 7,
      button: 0,
      clientX: 150,
      clientY: 75,
    });
    expect(capture).toHaveBeenCalledWith(7);
    expect(onChange).toHaveBeenLastCalledWith({
      focalX: 25,
      focalY: 25,
    });
    fireEvent.pointerMove(slider, {
      pointerId: 7,
      clientX: 251,
      clientY: 110,
    });
    expect(onChange).toHaveBeenLastCalledWith({
      focalX: 76,
      focalY: 60,
    });
    fireEvent.pointerMove(slider, {
      pointerId: 7,
      clientX: 400,
      clientY: 0,
    });
    expect(onChange).toHaveBeenLastCalledWith({
      focalX: 100,
      focalY: 0,
    });
    expect(onCommit).not.toHaveBeenCalled();
    fireEvent.pointerUp(slider, {
      pointerId: 7,
      clientX: 400,
      clientY: 0,
    });
    fireEvent.lostPointerCapture(slider, {
      pointerId: 7,
    });
    expect(onCommit).toHaveBeenCalledOnce();
    expect(onCommit).toHaveBeenCalledWith({
      focalX: 100,
      focalY: 0,
    });
  });

  it('ignores hover and secondary buttons, and commits the last point when a drag is cancelled', () => {
    const { slider, onChange, onCommit } = mount();
    fireEvent.pointerMove(slider, {
      clientX: 150,
      clientY: 75,
    });
    fireEvent.pointerDown(slider, {
      button: 2,
      clientX: 150,
      clientY: 75,
    });
    fireEvent.pointerUp(slider, {
      clientX: 150,
      clientY: 75,
    });
    expect(onChange).not.toHaveBeenCalled();
    expect(onCommit).not.toHaveBeenCalled();
    fireEvent.pointerDown(slider, {
      button: 0,
      clientX: 200,
      clientY: 100,
    });
    fireEvent.pointerCancel(slider);
    expect(onCommit).toHaveBeenCalledOnce();
    expect(onCommit).toHaveBeenCalledWith({
      focalX: 50,
      focalY: 50,
    });
  });

  it('moves 1% per arrow and 10% with Shift, then commits once when the key is released', () => {
    const { slider, onChange, onCommit } = mount();
    expect(
      fireEvent.keyDown(slider, {
        key: 'ArrowRight',
      }),
    ).toBe(false);
    expect(onChange).toHaveBeenLastCalledWith({
      focalX: 51,
      focalY: 50,
    });
    fireEvent.keyDown(slider, {
      key: 'ArrowDown',
      shiftKey: true,
    });
    fireEvent.keyDown(slider, {
      key: 'ArrowDown',
      shiftKey: true,
    });
    expect(onChange).toHaveBeenLastCalledWith({
      focalX: 51,
      focalY: 70,
    });
    expect(onCommit).not.toHaveBeenCalled();
    fireEvent.keyUp(slider, {
      key: 'ArrowDown',
    });
    expect(onCommit).toHaveBeenCalledOnce();
    expect(onCommit).toHaveBeenCalledWith({
      focalX: 51,
      focalY: 70,
    });
    expect(
      fireEvent.keyDown(slider, {
        key: 'Enter',
      }),
    ).toBe(true);
    expect(onChange).toHaveBeenCalledTimes(3);
  });

  it('clamps keyboard movement to the image edges', () => {
    const { slider, onChange } = mount({
      focalX: 96,
      focalY: 4,
    });
    fireEvent.keyDown(slider, {
      key: 'ArrowRight',
      shiftKey: true,
    });
    fireEvent.keyDown(slider, {
      key: 'ArrowUp',
      shiftKey: true,
    });
    expect(onChange).toHaveBeenLastCalledWith({
      focalX: 100,
      focalY: 0,
    });
  });

  it('is keyboard focusable with a visible focus style and describes both axes', () => {
    const { slider } = mount({
      focalX: 40,
      focalY: 60,
    });
    expect(slider).toHaveAttribute('tabindex', '0');
    expect(slider).toHaveClass('mantine-focus-auto');
    expect(slider).toHaveAttribute('aria-valuetext', 'Horizontal 40%, vertical 60%');
    expect(slider.querySelector('img')).toHaveAttribute('draggable', 'false');
  });

  it('ignores pointer and keyboard input while disabled', () => {
    const { slider, onChange, onCommit } = mount(undefined, true);
    expect(slider).toHaveAttribute('tabindex', '-1');
    expect(slider).toHaveAttribute('aria-disabled', 'true');
    fireEvent.pointerDown(slider, {
      button: 0,
      clientX: 150,
      clientY: 75,
    });
    fireEvent.pointerUp(slider);
    fireEvent.keyDown(slider, {
      key: 'ArrowLeft',
    });
    fireEvent.keyUp(slider, {
      key: 'ArrowLeft',
    });
    expect(onChange).not.toHaveBeenCalled();
    expect(onCommit).not.toHaveBeenCalled();
  });
});
