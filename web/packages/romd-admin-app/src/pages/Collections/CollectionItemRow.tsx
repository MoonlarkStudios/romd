import {
  ActionIcon,
  Box,
  Button,
  Group,
  Image,
  Paper,
  Popover,
  Stack,
  Text,
  Textarea,
  Tooltip,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { CollectionItemDto } from '@romd/admin-api-client';
import { IconArrowDown, IconArrowUp, IconNote, IconTrash } from '@tabler/icons-react';
import { useState } from 'react';

interface CollectionItemRowProps {
  item: CollectionItemDto;
  index: number;
  total: number;
  canEdit: boolean;
  busy: boolean;
  onMove: (index: number, direction: -1 | 1) => void;
  onRemove: (titleId: string) => void;
  onSaveNote: (note: string | null) => Promise<unknown>;
}

export function CollectionItemRow({
  item,
  index,
  total,
  canEdit,
  busy,
  onMove,
  onRemove,
  onSaveNote,
}: CollectionItemRowProps) {
  const [noteOpen, setNoteOpen] = useState(false);
  const [noteDraft, setNoteDraft] = useState(item.note ?? '');
  const [savingNote, setSavingNote] = useState(false);

  const handleSaveNote = async () => {
    setSavingNote(true);
    try {
      await onSaveNote(noteDraft.trim() || null);
      setNoteOpen(false);
    } catch {
      notifications.show({
        color: 'red',
        message: 'Failed to save the note.',
      });
    } finally {
      setSavingNote(false);
    }
  };

  return (
    <Paper
      withBorder
      p="xs"
    >
      <Group
        gap="sm"
        wrap="nowrap"
      >
        <Text
          size="sm"
          c="dimmed"
          w={20}
          ta="center"
          style={{
            fontVariantNumeric: 'tabular-nums',
          }}
        >
          {index + 1}
        </Text>

        <Image
          alt=""
          src={item.coverUrl ?? undefined}
          w={36}
          h={48}
          radius="sm"
          fallbackSrc="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg'/%3E"
          style={{
            flexShrink: 0,
            background: 'var(--mantine-color-default)',
          }}
        />

        <Box
          style={{
            flex: 1,
            minWidth: 0,
          }}
        >
          <Text
            size="sm"
            fw={500}
            truncate
          >
            {item.titleName}
          </Text>
          {item.note && (
            <Text
              size="xs"
              c="dimmed"
              lineClamp={1}
            >
              {item.note}
            </Text>
          )}
        </Box>

        {canEdit && (
          <Group
            gap={2}
            wrap="nowrap"
          >
            <Tooltip label="Move up">
              <ActionIcon
                variant="subtle"
                color="gray"
                disabled={index === 0 || busy}
                aria-label={`Move ${item.titleName} up`}
                onClick={() => onMove(index, -1)}
              >
                <IconArrowUp size={16} />
              </ActionIcon>
            </Tooltip>
            <Tooltip label="Move down">
              <ActionIcon
                variant="subtle"
                color="gray"
                disabled={index === total - 1 || busy}
                aria-label={`Move ${item.titleName} down`}
                onClick={() => onMove(index, 1)}
              >
                <IconArrowDown size={16} />
              </ActionIcon>
            </Tooltip>

            <Popover
              opened={noteOpen}
              onChange={setNoteOpen}
              width={280}
              position="bottom-end"
              withArrow
            >
              <Popover.Target>
                <Tooltip label={item.note ? 'Edit note' : 'Add note'}>
                  <ActionIcon
                    aria-label={`Edit note for ${item.titleName}`}
                    disabled={busy}
                    variant="subtle"
                    color={item.note ? 'blue' : 'gray'}
                    onClick={() => {
                      setNoteDraft(item.note ?? '');
                      setNoteOpen(true);
                    }}
                  >
                    <IconNote size={16} />
                  </ActionIcon>
                </Tooltip>
              </Popover.Target>
              <Popover.Dropdown>
                <Stack gap="xs">
                  <Textarea
                    label="Note"
                    placeholder="Why is this title here?"
                    autosize
                    minRows={2}
                    maxRows={5}
                    value={noteDraft}
                    onChange={(e) => setNoteDraft(e.currentTarget.value)}
                    data-autofocus
                  />
                  <Group
                    gap="xs"
                    justify="flex-end"
                  >
                    <Button
                      variant="subtle"
                      size="xs"
                      onClick={() => setNoteOpen(false)}
                    >
                      Cancel
                    </Button>
                    <Button
                      size="xs"
                      disabled={busy}
                      loading={savingNote}
                      onClick={handleSaveNote}
                    >
                      Save
                    </Button>
                  </Group>
                </Stack>
              </Popover.Dropdown>
            </Popover>

            <Tooltip label="Remove from collection">
              <ActionIcon
                aria-label={`Remove ${item.titleName}`}
                variant="subtle"
                color="red"
                disabled={busy}
                onClick={() => onRemove(item.titleId)}
              >
                <IconTrash size={16} />
              </ActionIcon>
            </Tooltip>
          </Group>
        )}
      </Group>
    </Paper>
  );
}
