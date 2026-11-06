import { MantineProvider, Modal } from '@mantine/core';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { ActivityCenter } from '../components/Activity/ActivityCenter';
import { render as renderWithProviders } from '../test/utils/render';
import { type KeyboardShortcut, useKeyboardShortcuts } from './useKeyboardShortcuts';

vi.mock('./api/useJobs', () => ({
  useJobs: () => ({
    data: [],
    isLoading: false,
  }),
}));
vi.mock('./api/useJobActions', () => ({
  useCancelJob: () => ({
    mutate: vi.fn(),
  }),
  useRetryJob: () => ({
    mutateAsync: vi.fn(),
  }),
  useArchiveJob: () => ({
    mutateAsync: vi.fn(),
  }),
}));
vi.mock('./realtime/JobSocketContext', () => ({
  useConnectionStatus: () => 'connected',
}));

function WindowShortcuts({
  shortcuts,
  children,
}: {
  shortcuts: KeyboardShortcut[];
  children?: ReactNode;
}) {
  useKeyboardShortcuts(shortcuts);
  return <>{children}</>;
}

function Workspace({
  shortcuts,
  mounted = true,
  children,
}: {
  shortcuts: KeyboardShortcut[];
  mounted?: boolean;
  children?: ReactNode;
}) {
  const ref = useKeyboardShortcuts(shortcuts, {
    scope: 'container',
  });
  return (
    <>
      <button type="button">Outside</button>
      {mounted && (
        <section
          ref={ref}
          aria-label="Workspace"
        >
          <button type="button">Inside</button>
          {children}
        </section>
      )}
    </>
  );
}

const single = (key: string, extra: Partial<KeyboardShortcut> = {}) => {
  const action = vi.fn();
  return {
    action,
    shortcuts: [
      {
        key,
        action,
        ...extra,
      },
    ],
  };
};

describe('ActivityCenter Cmd+J', () => {
  it('toggles the activity drawer with Cmd or Ctrl+J, including while the drawer is open, but not while typing', async () => {
    renderWithProviders(
      <>
        <input aria-label="Search" />
        <ActivityCenter />
      </>,
    );
    fireEvent.keyDown(document.body, {
      key: 'j',
    });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(
      fireEvent.keyDown(document.body, {
        key: 'j',
        metaKey: true,
      }),
    ).toBe(false);
    const drawer = await screen.findByRole('dialog');
    fireEvent.keyDown(drawer, {
      key: 'j',
      ctrlKey: true,
    });
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    fireEvent.keyDown(
      screen.getByRole('textbox', {
        name: 'Search',
      }),
      {
        key: 'j',
        metaKey: true,
      },
    );
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});

describe('useKeyboardShortcuts typing targets', () => {
  it.each([
    [
      'input',
      <input
        key="input"
        aria-label="Field"
      />,
    ],
    [
      'textarea',
      <textarea
        key="textarea"
        aria-label="Field"
      />,
    ],
    [
      'select',
      <select
        key="select"
        aria-label="Field"
      >
        <option>One</option>
      </select>,
    ],
  ])('ignores single-key shortcuts typed into a %s', (_, field) => {
    const { action, shortcuts } = single('j');
    render(<WindowShortcuts shortcuts={shortcuts}>{field}</WindowShortcuts>);
    fireEvent.keyDown(screen.getByLabelText('Field'), {
      key: 'j',
    });
    expect(action).not.toHaveBeenCalled();
  });

  it('ignores shortcuts typed into editable content and elements inside it', () => {
    const { action, shortcuts } = single('n');
    render(
      <WindowShortcuts shortcuts={shortcuts}>
        <div
          contentEditable
          suppressContentEditableWarning
        >
          <strong>Draft</strong>
        </div>
      </WindowShortcuts>,
    );
    const bold = screen.getByText('Draft');
    Object.defineProperty(bold, 'isContentEditable', {
      value: true,
    });
    fireEvent.keyDown(bold, {
      key: 'n',
    });
    expect(action).not.toHaveBeenCalled();
  });

  it('still runs Escape shortcuts while typing', () => {
    const { action, shortcuts } = single('Escape');
    render(
      <WindowShortcuts shortcuts={shortcuts}>
        <input aria-label="Field" />
      </WindowShortcuts>,
    );
    fireEvent.keyDown(screen.getByLabelText('Field'), {
      key: 'Escape',
    });
    expect(action).toHaveBeenCalledOnce();
  });
});

describe('useKeyboardShortcuts overlays', () => {
  it.each([
    [
      'a dialog',
      <div
        key="o"
        role="dialog"
        aria-label="Overlay"
      />,
    ],
    [
      'an alert dialog',
      <div
        key="o"
        role="alertdialog"
        aria-modal="true"
        aria-label="Overlay"
      />,
    ],
    [
      'a menu',
      <div
        key="o"
        role="menu"
        aria-label="Overlay"
      />,
    ],
    [
      'a native dialog',
      <dialog
        key="o"
        open
        aria-label="Overlay"
      />,
    ],
  ])('suppresses single-key shortcuts while %s is open', (_, overlay) => {
    const { action, shortcuts } = single('n');
    render(<WindowShortcuts shortcuts={shortcuts}>{overlay}</WindowShortcuts>);
    fireEvent.keyDown(document.body, {
      key: 'n',
    });
    expect(action).not.toHaveBeenCalled();
  });

  it('suppresses single-key shortcuts while a Mantine modal is open and resumes when it closes', async () => {
    const { action, shortcuts } = single('k');
    const view = render(
      <MantineProvider env="test">
        <Workspace shortcuts={shortcuts}>
          <Modal
            opened
            onClose={() => undefined}
            title="Rename"
          />
        </Workspace>
      </MantineProvider>,
    );
    fireEvent.keyDown(
      screen.getByRole('button', {
        name: 'Inside',
      }),
      {
        key: 'k',
      },
    );
    expect(action).not.toHaveBeenCalled();
    view.rerender(
      <MantineProvider env="test">
        <Workspace shortcuts={shortcuts}>
          <Modal
            opened={false}
            onClose={() => undefined}
            title="Rename"
          />
        </Workspace>
      </MantineProvider>,
    );
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    fireEvent.keyDown(
      screen.getByRole('button', {
        name: 'Inside',
      }),
      {
        key: 'k',
      },
    );
    expect(action).toHaveBeenCalledOnce();
  });

  it('ignores hidden overlays and keeps chorded shortcuts available while an overlay is open', () => {
    const next = vi.fn();
    const palette = vi.fn();
    render(
      <WindowShortcuts
        shortcuts={[
          {
            key: 'n',
            action: next,
          },
          {
            key: 'k',
            meta: true,
            action: palette,
          },
        ]}
      >
        <div
          role="dialog"
          aria-label="Kept mounted"
          hidden
        />
        <div
          style={{
            display: 'none',
          }}
        >
          <div
            role="menu"
            aria-label="Closed menu"
          />
        </div>
      </WindowShortcuts>,
    );
    fireEvent.keyDown(document.body, {
      key: 'n',
    });
    expect(next).toHaveBeenCalledOnce();
    render(
      <div
        role="dialog"
        aria-label="Open"
      />,
    );
    fireEvent.keyDown(document.body, {
      key: 'n',
    });
    fireEvent.keyDown(document.body, {
      key: 'k',
      metaKey: true,
    });
    expect(next).toHaveBeenCalledOnce();
    expect(palette).toHaveBeenCalledOnce();
  });

  it('keeps container shortcuts when the container itself is inside the open dialog', () => {
    const { action, shortcuts } = single('j');
    render(
      <div
        role="dialog"
        aria-label="Workbench dialog"
      >
        <Workspace shortcuts={shortcuts} />
      </div>,
    );
    fireEvent.keyDown(
      screen.getByRole('button', {
        name: 'Inside',
      }),
      {
        key: 'j',
      },
    );
    expect(action).toHaveBeenCalledOnce();
  });
});

describe('useKeyboardShortcuts container scope', () => {
  it('binds when a conditionally rendered container mounts and follows a replaced container', () => {
    const { action, shortcuts } = single('j');
    const view = render(
      <Workspace
        shortcuts={shortcuts}
        mounted={false}
      />,
    );
    view.rerender(<Workspace shortcuts={shortcuts} />);
    fireEvent.keyDown(
      screen.getByRole('button', {
        name: 'Inside',
      }),
      {
        key: 'j',
      },
    );
    expect(action).toHaveBeenCalledOnce();
    view.rerender(
      <Workspace
        shortcuts={shortcuts}
        mounted={false}
      />,
    );
    view.rerender(<Workspace shortcuts={shortcuts} />);
    fireEvent.keyDown(
      screen.getByRole('button', {
        name: 'Inside',
      }),
      {
        key: 'j',
      },
    );
    expect(action).toHaveBeenCalledTimes(2);
  });

  it('ignores events whose target is outside the container', () => {
    const { action, shortcuts } = single('j');
    render(<Workspace shortcuts={shortcuts} />);
    fireEvent.keyDown(
      screen.getByRole('button', {
        name: 'Outside',
      }),
      {
        key: 'j',
      },
    );
    fireEvent.keyDown(document.body, {
      key: 'j',
    });
    expect(action).not.toHaveBeenCalled();
    fireEvent.keyDown(
      screen.getByRole('region', {
        name: 'Workspace',
      }),
      {
        key: 'j',
      },
    );
    expect(action).toHaveBeenCalledOnce();
  });

  it('stops listening while disabled', () => {
    const action = vi.fn();
    function Disabled() {
      const ref = useKeyboardShortcuts(
        [
          {
            key: 'j',
            action,
          },
        ],
        {
          scope: 'container',
          enabled: false,
        },
      );
      return (
        <section
          ref={ref}
          aria-label="Workspace"
        />
      );
    }
    render(<Disabled />);
    fireEvent.keyDown(
      screen.getByRole('region', {
        name: 'Workspace',
      }),
      {
        key: 'j',
      },
    );
    expect(action).not.toHaveBeenCalled();
  });
});

describe('useKeyboardShortcuts modifiers', () => {
  it.each([
    'j',
    'k',
    'n',
    'Enter',
    '?',
  ])('never runs %s while Ctrl, Meta, or Alt is held unless declared', (key) => {
    const { action, shortcuts } = single(key);
    render(<Workspace shortcuts={shortcuts} />);
    const inside = screen.getByRole('button', {
      name: 'Inside',
    });
    fireEvent.keyDown(inside, {
      key,
      ctrlKey: true,
    });
    fireEvent.keyDown(inside, {
      key,
      metaKey: true,
    });
    fireEvent.keyDown(inside, {
      key,
      altKey: true,
    });
    expect(action).not.toHaveBeenCalled();
    fireEvent.keyDown(inside, {
      key,
      shiftKey: key === '?',
    });
    expect(action).toHaveBeenCalledOnce();
  });

  it('runs ? with the Shift it needs, but keeps Shift distinct for letters', () => {
    const help = vi.fn();
    const top = vi.fn();
    const bottom = vi.fn();
    render(
      <Workspace
        shortcuts={[
          {
            key: '?',
            action: help,
          },
          {
            key: 'g',
            action: top,
          },
          {
            key: 'G',
            shift: true,
            action: bottom,
          },
        ]}
      />,
    );
    const inside = screen.getByRole('button', {
      name: 'Inside',
    });
    fireEvent.keyDown(inside, {
      key: '?',
      shiftKey: true,
    });
    fireEvent.keyDown(inside, {
      key: 'G',
      shiftKey: true,
    });
    expect(help).toHaveBeenCalledOnce();
    expect(bottom).toHaveBeenCalledOnce();
    expect(top).not.toHaveBeenCalled();
    fireEvent.keyDown(inside, {
      key: 'g',
    });
    expect(top).toHaveBeenCalledOnce();
  });
});
