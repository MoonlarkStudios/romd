import { MantineProvider } from '@mantine/core';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { type KeyboardShortcut, useKeyboardShortcuts } from '../../hooks/useKeyboardShortcuts';
import { KeyboardShortcutsHelp } from './KeyboardShortcutsHelp';

const noop = () => undefined;
const shortcuts: KeyboardShortcut[] = [
  {
    key: 'j',
    description: 'Next title in the queue',
    action: noop,
  },
  {
    key: 'ArrowDown',
    description: 'Next title in the queue',
    action: noop,
  },
  {
    key: 'Enter',
    description: 'Open the current requirement',
    action: noop,
  },
  {
    key: 'j',
    meta: true,
    description: 'Toggle activity panel',
    action: noop,
  },
  {
    key: 'G',
    shift: true,
    description: 'Last title',
    action: noop,
  },
  {
    key: 'x',
    action: noop,
  },
];

function Workspace() {
  const [helpOpen, setHelpOpen] = useState(false);
  const all = [
    ...shortcuts,
    {
      key: '?',
      description: 'Show keyboard shortcuts',
      action: () => setHelpOpen(true),
    },
  ];
  const ref = useKeyboardShortcuts(all, {
    scope: 'container',
  });
  return (
    <div ref={ref}>
      <button type="button">Current requirement</button>
      <KeyboardShortcutsHelp
        shortcuts={all}
        opened={helpOpen}
        onClose={() => setHelpOpen(false)}
      />
    </div>
  );
}

describe('KeyboardShortcutsHelp', () => {
  it('lists described shortcuts with their keys in a titled dialog', () => {
    render(
      <MantineProvider env="test">
        <KeyboardShortcutsHelp
          shortcuts={shortcuts}
          opened
          onClose={noop}
        />
      </MantineProvider>,
    );
    const dialog = screen.getByRole('dialog', {
      name: 'Keyboard shortcuts',
    });
    const rows = within(dialog).getAllByRole('row').slice(1);
    expect(rows.map((row) => row.textContent)).toEqual([
      'jNext title in the queue',
      '↓Next title in the queue',
      'EnterOpen the current requirement',
      'CtrljToggle activity panel',
      'ShiftGLast title',
    ]);
    expect(Array.from(rows[3].querySelectorAll('kbd'), (key) => key.textContent)).toEqual([
      'Ctrl',
      'j',
    ]);
  });

  it('opens from ? inside the workspace and returns focus to the trigger when closed', async () => {
    render(
      <MantineProvider env="test">
        <Workspace />
      </MantineProvider>,
    );
    const trigger = screen.getByRole('button', {
      name: 'Current requirement',
    });
    trigger.focus();
    fireEvent.keyDown(trigger, {
      key: '?',
      shiftKey: true,
    });
    const dialog = await screen.findByRole('dialog', {
      name: 'Keyboard shortcuts',
    });
    expect(within(dialog).getByText('Show keyboard shortcuts')).toBeInTheDocument();
    fireEvent.keyDown(dialog, {
      key: 'Escape',
    });
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(trigger).toHaveFocus());
  });
});
