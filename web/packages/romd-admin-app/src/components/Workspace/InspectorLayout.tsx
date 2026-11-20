import { useReducedMotion } from '@mantine/hooks';
import { type CSSProperties, type ReactNode, useLayoutEffect, useRef, useState } from 'react';
import classes from './InspectorLayout.module.css';

interface InspectorLayoutProps {
  children: ReactNode;
  inspector?: ReactNode;
  wide: boolean;
  narrow?: 'hide' | 'stack';
  width?: string;
  animateList?: boolean;
}

export function InspectorLayout({
  children,
  inspector,
  wide,
  narrow = 'hide',
  width,
  animateList = true,
}: InspectorLayoutProps) {
  const reducedMotion = useReducedMotion();
  const stacked = !wide && narrow === 'stack';
  const open = !!inspector && (wide || stacked);
  const [lastInspector, setLastInspector] = useState<ReactNode>();
  const column = useRef<HTMLDivElement>(null);

  useLayoutEffect(() => {
    const element = column.current;
    if (!open || !wide || !element) return;
    const measureOffset = () => {
      const top = Math.max(0, element.getBoundingClientRect().top);
      element.style.setProperty('--inspector-start-offset', `${Math.round(top)}px`);
    };
    measureOffset();
    window.addEventListener('resize', measureOffset);
    return () => window.removeEventListener('resize', measureOffset);
  }, [
    open,
    wide,
  ]);

  // Keep the visible content in place until the column has finished closing.
  if (open && inspector !== lastInspector) setLastInspector(inspector);
  const closing = !open && wide && !reducedMotion;
  if (!open && !closing && lastInspector) setLastInspector(undefined);

  useLayoutEffect(() => {
    if (!closing || !lastInspector || !column.current) return;
    // A transition may never start when a preview opens and closes before the next paint.
    const duration = getComputedStyle(column.current).transitionDuration;
    const ms = (Number.parseFloat(duration) || 0) * (duration.endsWith('ms') ? 1 : 1000);
    const timeout = window.setTimeout(() => setLastInspector(undefined), ms * 2);
    return () => window.clearTimeout(timeout);
  }, [
    closing,
    lastInspector,
  ]);

  return (
    <div
      className={classes.layout}
      data-open={open || undefined}
      data-snap-width={!animateList || undefined}
      data-inspector-stacked={stacked || undefined}
      style={
        width
          ? ({
              '--inspector-width': width,
            } as CSSProperties)
          : undefined
      }
    >
      <div className={classes.list}>{children}</div>
      <div
        ref={column}
        className={classes.column}
        aria-hidden={!open || undefined}
        inert={!open}
        onTransitionEnd={(event) => {
          if (!open && event.target === event.currentTarget && event.propertyName === 'opacity')
            setLastInspector(undefined);
        }}
      >
        <div className={classes.content}>{open ? inspector : closing ? lastInspector : null}</div>
      </div>
    </div>
  );
}
