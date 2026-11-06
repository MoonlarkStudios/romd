import { Group, Kbd, Modal, Table } from '@mantine/core';
import type { KeyboardShortcut } from '../../hooks/useKeyboardShortcuts';

const keyNames: Readonly<Record<string, string>> = {
  ' ': 'Space',
  ArrowUp: '↑',
  ArrowDown: '↓',
  ArrowLeft: '←',
  ArrowRight: '→',
  Escape: 'Esc',
};

/** Cmd/Ctrl shortcuts accept either key, so Ctrl is accurate on every platform. */
const shortcutKeys = ({ key, meta, alt, shift }: KeyboardShortcut) =>
  [
    meta && 'Ctrl',
    alt && 'Alt',
    shift && 'Shift',
    keyNames[key] ?? key,
  ].filter((part): part is string => Boolean(part));

export interface KeyboardShortcutsHelpProps {
  /** The same shortcuts passed to `useKeyboardShortcuts`; only described ones are listed. */
  shortcuts: readonly KeyboardShortcut[];
  opened: boolean;
  onClose: () => void;
}

/** Lists the available shortcuts. Open it from a `?` shortcut; closing returns focus to the element that opened it. */
export function KeyboardShortcutsHelp({ shortcuts, opened, onClose }: KeyboardShortcutsHelpProps) {
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title="Keyboard shortcuts"
      centered
    >
      <Table>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Keys</Table.Th>
            <Table.Th>Action</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {shortcuts
            .filter((shortcut) => shortcut.description)
            .map((shortcut) => {
              const keys = shortcutKeys(shortcut);
              return (
                <Table.Tr key={`${keys.join('+')}:${shortcut.description}`}>
                  <Table.Td>
                    <Group
                      gap={4}
                      wrap="nowrap"
                    >
                      {keys.map((part) => (
                        <Kbd key={part}>{part}</Kbd>
                      ))}
                    </Group>
                  </Table.Td>
                  <Table.Td>{shortcut.description}</Table.Td>
                </Table.Tr>
              );
            })}
        </Table.Tbody>
      </Table>
    </Modal>
  );
}
