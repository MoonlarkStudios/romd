import { MantineProvider } from '@mantine/core';
import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  defaultPreviewDevice,
  PreviewDeviceSwitcher,
  usePreviewDevice,
} from './PreviewDeviceSwitcher';

function Column() {
  const { ref, device, choose } = usePreviewDevice();
  return (
    <div ref={ref}>
      <PreviewDeviceSwitcher
        value={device}
        onChange={choose}
      />
      <p>Showing {device}</p>
    </div>
  );
}

const mount = (ui: React.ReactNode) => render(<MantineProvider env="test">{ui}</MantineProvider>);

afterEach(() => {
  sessionStorage.clear();
  vi.restoreAllMocks();
});

describe('defaultPreviewDevice', () => {
  it('picks the widest device that renders at half size or larger', () => {
    expect(defaultPreviewDevice(720)).toBe('desktop');
    expect(defaultPreviewDevice(719)).toBe('tablet');
    expect(defaultPreviewDevice(417)).toBe('tablet');
    expect(defaultPreviewDevice(416)).toBe('mobile');
    expect(defaultPreviewDevice(0)).toBe('desktop');
  });
});

describe('PreviewDeviceSwitcher', () => {
  it('offers labelled desktop, tablet, and mobile choices', () => {
    const onChange = vi.fn();
    mount(
      <PreviewDeviceSwitcher
        value="tablet"
        onChange={onChange}
      />,
    );
    expect(
      screen.getByRole('radiogroup', {
        name: 'Preview device',
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('radio', {
        name: 'Tablet',
      }),
    ).toBeChecked();
    fireEvent.click(
      screen.getByRole('radio', {
        name: 'Mobile',
      }),
    );
    expect(onChange).toHaveBeenCalledWith('mobile');
  });

  it('defaults from the column width and keeps the curator choice for the session', () => {
    vi.spyOn(Element.prototype, 'clientWidth', 'get').mockReturnValue(600);
    const first = mount(<Column />);
    expect(screen.getByText('Showing tablet')).toBeInTheDocument();
    fireEvent.click(
      screen.getByRole('radio', {
        name: 'Mobile',
      }),
    );
    expect(screen.getByText('Showing mobile')).toBeInTheDocument();
    first.unmount();
    vi.spyOn(Element.prototype, 'clientWidth', 'get').mockReturnValue(1600);
    mount(<Column />);
    expect(screen.getByText('Showing mobile')).toBeInTheDocument();
  });

  it('ignores an unknown stored device', () => {
    sessionStorage.setItem('romd:artwork-preview-device', 'watch');
    vi.spyOn(Element.prototype, 'clientWidth', 'get').mockReturnValue(1600);
    mount(<Column />);
    expect(screen.getByText('Showing desktop')).toBeInTheDocument();
  });
});
