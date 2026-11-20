import { type KeyboardEvent, type PointerEvent, useRef, useState } from 'react';
import classes from './ArtworkWorkspace.module.css';

export interface FocalPoint {
  focalX: number;
  focalY: number;
}

/** A consumer frame the image is cropped to, such as the desktop game details backdrop, by its width/height ratio. */
export interface CropFrame {
  label: string;
  aspect: number;
}

/** The part of an image (as fractions of its width and height) a cover-fit frame shows at a focal point. */
export function cropRegion(imageAspect: number, frameAspect: number, point: FocalPoint) {
  if (imageAspect > frameAspect) {
    const width = frameAspect / imageAspect;
    return {
      left: (1 - width) * (point.focalX / 100),
      top: 0,
      width,
      height: 1,
    };
  }
  const height = imageAspect / frameAspect;
  return {
    left: 0,
    top: (1 - height) * (point.focalY / 100),
    width: 1,
    height,
  };
}

export interface FocalPointEditorProps {
  src: string;
  value: FocalPoint;
  /** Consumer frames to outline over the image; the area outside `activeFrame` is dimmed. */
  frames?: readonly CropFrame[];
  activeFrame?: string;
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
  frames = [],
  activeFrame,
}: FocalPointEditorProps) {
  const [imageAspect, setImageAspect] = useState(0);
  const frame = frames.find((item) => item.label === activeFrame) ?? frames[0];
  const region = frame && imageAspect > 0 ? cropRegion(imageAspect, frame.aspect, value) : null;
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
        overflow: 'hidden',
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
        onLoad={(event) => {
          const { naturalWidth, naturalHeight } = event.currentTarget;
          setImageAspect(naturalWidth > 0 && naturalHeight > 0 ? naturalWidth / naturalHeight : 0);
        }}
        style={{
          display: 'block',
          maxWidth: '100%',
          maxHeight,
        }}
      />
      {frame && region && (
        <span
          aria-hidden="true"
          data-crop-frame={frame.label}
          className={classes.cropOutline}
          style={{
            left: `${region.left * 100}%`,
            top: `${region.top * 100}%`,
            width: `${region.width * 100}%`,
            height: `${region.height * 100}%`,
          }}
        />
      )}
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
          zIndex: 2,
        }}
      />
    </div>
  );
}

/** Select a consumer crop without changing the focal point. */
export function CropPreviews({
  src,
  frames,
  value,
  activeFrame,
  onFrameChange,
}: {
  src: string;
  frames: readonly CropFrame[];
  value: FocalPoint;
  activeFrame?: string;
  onFrameChange: (label: string) => void;
}) {
  if (!frames.length) return null;
  const selected = frames.find((frame) => frame.label === activeFrame) ?? frames[0];
  return (
    <fieldset
      className={classes.cropPreviews}
      aria-label="Crop previews"
    >
      {frames.map((frame) => (
        <button
          key={frame.label}
          type="button"
          className={classes.cropPreview}
          style={{
            width: Math.max(56, Math.min(144, Math.round(56 * frame.aspect))),
          }}
          aria-label={`Preview ${frame.label} crop`}
          aria-pressed={frame === selected}
          onClick={() => onFrameChange(frame.label)}
        >
          <span
            className={classes.cropPreviewImage}
            style={{
              aspectRatio: frame.aspect,
            }}
          >
            <img
              src={src}
              alt=""
              style={{
                objectPosition: `${value.focalX}% ${value.focalY}%`,
              }}
            />
          </span>
          <span>{frame.label}</span>
        </button>
      ))}
    </fieldset>
  );
}
