import { SegmentedControl } from '@mantine/core';
import { deviceScale, type PreviewDevice, previewDevices } from '@romd/consumer-ui';
import { useCallback, useLayoutEffect, useState } from 'react';

const devices = [
  'desktop',
  'tablet',
  'mobile',
] as const satisfies readonly PreviewDevice[];
const storageKey = 'romd:artwork-preview-device';
const isPreviewDevice = (value: string | null): value is PreviewDevice =>
  devices.some((device) => device === value);

/** The widest device that still renders at half size or larger in the available width; unmeasured space shows desktop. */
export function defaultPreviewDevice(available: number): PreviewDevice {
  return available > 0
    ? (devices.find((device) => deviceScale(available, previewDevices[device].width) >= 0.5) ??
        'mobile')
    : 'desktop';
}

/**
 * Measures the preview column given the returned callback ref. The device follows the column width until the
 * curator picks one; that choice lasts for the browser session.
 */
export function usePreviewDevice() {
  const [column, setColumn] = useState<HTMLElement | null>(null);
  const [available, setAvailable] = useState(0);
  const [chosen, setChosen] = useState<PreviewDevice | null>(() => {
    const stored = sessionStorage.getItem(storageKey);
    return isPreviewDevice(stored) ? stored : null;
  });
  useLayoutEffect(() => {
    if (!column) return;
    const measure = () => setAvailable(column.clientWidth);
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(measure);
    observer.observe(column);
    return () => observer.disconnect();
  }, [
    column,
  ]);
  const choose = useCallback((device: PreviewDevice) => {
    sessionStorage.setItem(storageKey, device);
    setChosen(device);
  }, []);
  return {
    ref: setColumn,
    device: chosen ?? defaultPreviewDevice(available),
    choose,
  };
}

export function PreviewDeviceSwitcher({
  value,
  onChange,
}: {
  value: PreviewDevice;
  onChange: (device: PreviewDevice) => void;
}) {
  return (
    <SegmentedControl
      aria-label="Preview device"
      size="xs"
      value={value}
      onChange={(next) => {
        if (isPreviewDevice(next)) onChange(next);
      }}
      data={devices.map((device) => ({
        value: device,
        label: previewDevices[device].label,
      }))}
    />
  );
}
