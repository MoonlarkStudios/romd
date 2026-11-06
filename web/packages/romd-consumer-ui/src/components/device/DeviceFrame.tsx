import { type CSSProperties, type ReactNode, useLayoutEffect, useRef, useState } from 'react';

export type PreviewDevice = 'desktop' | 'tablet' | 'mobile';

export const previewDevices: Readonly<
  Record<
    PreviewDevice,
    {
      label: string;
      width: number;
      height: number;
    }
  >
> = {
  desktop: {
    label: 'Desktop',
    width: 1440,
    height: 900,
  },
  tablet: {
    label: 'Tablet',
    width: 834,
    height: 1194,
  },
  mobile: {
    label: 'Mobile',
    width: 390,
    height: 844,
  },
};

/** Fits the intrinsic width into the available width without enlarging; unmeasured space renders at full size. */
export function deviceScale(available: number, intrinsic: number): number {
  return available > 0 ? Math.min(1, available / intrinsic) : 1;
}

export interface DeviceFrameProps {
  device: PreviewDevice;
  /** Clip to the device's viewport height, for hero-type surfaces. */
  fixedHeight?: boolean;
  children: ReactNode;
}

/**
 * Renders children at the device's real width, so container queries inside see that width, then scales the result
 * down to the available width. The frame reserves the scaled height so content below does not jump, and sets
 * `--romd-viewport-height` so consumer surfaces size against the device viewport instead of the host window.
 * The screen is inert: its consumer headings and actions stay out of the host's focus order and accessibility tree,
 * and the figure's label names the preview.
 */
export function DeviceFrame({ device, fixedHeight = false, children }: DeviceFrameProps) {
  const { label, width, height } = previewDevices[device];
  const frame = useRef<HTMLElement>(null);
  const screen = useRef<HTMLDivElement>(null);
  const [available, setAvailable] = useState(0);
  const [contentHeight, setContentHeight] = useState(0);
  useLayoutEffect(() => {
    const outer = frame.current;
    const inner = screen.current;
    if (!outer || !inner) return;
    const measure = () => {
      setAvailable(outer.clientWidth);
      setContentHeight(inner.offsetHeight);
    };
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(measure);
    observer.observe(outer);
    observer.observe(inner);
    return () => observer.disconnect();
  }, []);
  const scale = deviceScale(available, width);
  const screenHeight = fixedHeight ? height : contentHeight;
  const screenStyle: CSSProperties & Record<'--romd-viewport-height', string> = {
    width,
    height: fixedHeight ? height : undefined,
    overflow: 'hidden',
    transform: `scale(${scale})`,
    transformOrigin: 'top left',
    containerType: 'inline-size',
    '--romd-viewport-height': `${height}px`,
  };
  return (
    <figure
      ref={frame}
      aria-label={`${label} preview, ${width} pixels wide`}
      style={{
        margin: 0,
        width: '100%',
        height: screenHeight > 0 ? Math.ceil(screenHeight * scale) : undefined,
        overflow: 'hidden',
        contain: 'inline-size',
      }}
    >
      <div
        ref={screen}
        inert
        style={screenStyle}
      >
        {children}
      </div>
    </figure>
  );
}
