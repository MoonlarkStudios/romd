import { type KeyboardEvent, type PointerEvent, useRef } from 'react';

export interface FocalPoint {
  focalX: number;
  focalY: number;
}

export interface FocalPointEditorProps {
  src: string;
  value: FocalPoint;
  /** Every intermediate point while dragging or pressing arrow keys. */
  onChange: (value: FocalPoint) => void;
  /** The settled point, once per drag or key press sequence, so callers save once. */
  onCommit: (value: FocalPoint) => void;
  disabled?: boolean;
  maxHeight?: number;
}

const arrowSteps: Readonly<
  Record<
    string,
    readonly [
      number,
      number,
    ]
  >
> = {
  ArrowLeft: [
    -1,
    0,
  ],
  ArrowRight: [
    1,
    0,
  ],
  ArrowUp: [
    0,
    -1,
  ],
  ArrowDown: [
    0,
    1,
  ],
};

const percent = (value: number) => Math.min(100, Math.max(0, Math.round(value)));

function pointAt(surface: HTMLElement, clientX: number, clientY: number): FocalPoint {
  const bounds = surface.getBoundingClientRect();
  return {
    focalX: percent(((clientX - bounds.left) / bounds.width) * 100),
    focalY: percent(((clientY - bounds.top) / bounds.height) * 100),
  };
}

/** Drag the marker, or use arrow keys (Shift for 10%), to place the focal point over the image. */
export function FocalPointEditor({
  src,
  value,
  onChange,
  onCommit,
  disabled = false,
  maxHeight,
}: FocalPointEditorProps) {
  const dragged = useRef<FocalPoint | null>(null);
  const keyed = useRef<FocalPoint | null>(null);
  const drag = (event: PointerEvent<HTMLDivElement>) => {
    const point = pointAt(event.currentTarget, event.clientX, event.clientY);
    dragged.current = point;
    onChange(point);
  };
  const settle = (pending: { current: FocalPoint | null }) => {
    const point = pending.current;
    pending.current = null;
    if (point) onCommit(point);
  };
  const step = (event: KeyboardEvent<HTMLDivElement>) => {
    const direction = arrowSteps[event.key];
    if (disabled || !direction) return;
    event.preventDefault();
    const distance = event.shiftKey ? 10 : 1;
    const from = keyed.current ?? value;
    const point = {
      focalX: percent(from.focalX + direction[0] * distance),
      focalY: percent(from.focalY + direction[1] * distance),
    };
    keyed.current = point;
    onChange(point);
  };
  return (
    <div
      role="slider"
      tabIndex={disabled ? -1 : 0}
      aria-label="Focal point"
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={value.focalX}
      aria-valuetext={`Horizontal ${value.focalX}%, vertical ${value.focalY}%`}
      aria-disabled={disabled || undefined}
      className="mantine-focus-auto"
      style={{
        position: 'relative',
        display: 'inline-block',
        maxWidth: '100%',
        lineHeight: 0,
        touchAction: 'none',
        userSelect: 'none',
        cursor: disabled ? 'default' : 'crosshair',
      }}
      onPointerDown={(event) => {
        if (disabled || event.button !== 0) return;
        event.currentTarget.setPointerCapture(event.pointerId);
        drag(event);
      }}
      onPointerMove={(event) => {
        if (dragged.current) drag(event);
      }}
      onPointerUp={() => settle(dragged)}
      onPointerCancel={() => settle(dragged)}
      onLostPointerCapture={() => settle(dragged)}
      onKeyDown={step}
      onKeyUp={(event) => {
        if (arrowSteps[event.key]) settle(keyed);
      }}
      onBlur={() => settle(keyed)}
    >
      <img
        src={src}
        alt=""
        draggable={false}
        style={{
          display: 'block',
          maxWidth: '100%',
          maxHeight,
        }}
      />
      <span
        aria-hidden="true"
        style={{
          position: 'absolute',
          left: `${value.focalX}%`,
          top: `${value.focalY}%`,
          width: 24,
          height: 24,
          transform: 'translate(-50%, -50%)',
          borderRadius: '50%',
          border: '2px solid var(--mantine-color-white)',
          boxShadow:
            '0 0 0 1px var(--mantine-color-black), inset 0 0 0 1px var(--mantine-color-black)',
          pointerEvents: 'none',
        }}
      />
    </div>
  );
}
