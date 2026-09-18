import {
  ActionIcon,
  Badge,
  Box,
  CopyButton,
  Group,
  Stack,
  Table,
  Text,
  Tooltip,
} from '@mantine/core';
import type { DatDisk, DatGame, DatRom } from '@romd/admin-api-client';
import { IconCheck, IconChevronRight, IconCopy } from '@tabler/icons-react';
import { useState } from 'react';
import { formatBytes } from '../../utils/format';

/** DAT dump statuses are open-ended; color only the known cases, show everything else neutral. */
const BAD_STATUSES = new Set([
  'baddump',
  'nodump',
]);

function statusColor(status: string): string {
  const lower = status.toLowerCase();
  if (BAD_STATUSES.has(lower)) return 'red';
  if (lower === 'verified') return 'green';
  return 'gray';
}

/** A dump status is only worth a badge when the DAT actually asserts one (good ROMs omit it). */
function StatusBadge({ status }: { status?: string | null }) {
  if (!status) {
    return (
      <Text
        size="xs"
        c="dimmed"
      >
        —
      </Text>
    );
  }
  return (
    <Badge
      size="xs"
      variant="light"
      color={statusColor(status)}
      radius="sm"
    >
      {status}
    </Badge>
  );
}

function HashLine({ label, value }: { label: string; value: string }) {
  return (
    <Group
      gap={6}
      wrap="nowrap"
    >
      <Text
        size="xs"
        c="dimmed"
        w={34}
        style={{
          flexShrink: 0,
        }}
      >
        {label}
      </Text>
      <Text
        size="xs"
        ff="monospace"
        truncate="end"
        title={value}
        style={{
          flex: 1,
          minWidth: 0,
        }}
      >
        {value}
      </Text>
      <CopyButton
        value={value}
        timeout={2000}
      >
        {({ copied, copy }) => (
          <Tooltip
            label={copied ? 'Copied!' : 'Copy'}
            withArrow
          >
            <ActionIcon
              aria-label={`Copy ${label}`}
              size="xs"
              variant="subtle"
              color={copied ? 'teal' : 'gray'}
              onClick={copy}
            >
              {copied ? <IconCheck size={12} /> : <IconCopy size={12} />}
            </ActionIcon>
          </Tooltip>
        )}
      </CopyButton>
    </Group>
  );
}

/**
 * The CRC/MD5/SHA1 that make a DAT entry verifiable, stacked in one column so each
 * hash gets the full width it needs instead of competing for a narrow cell.
 */
function HashList({
  crc,
  md5,
  sha1,
}: {
  crc?: string | null;
  md5?: string | null;
  sha1?: string | null;
}) {
  const entries: Array<
    [
      string,
      string,
    ]
  > = [];
  if (crc)
    entries.push([
      'CRC',
      crc,
    ]);
  if (md5)
    entries.push([
      'MD5',
      md5,
    ]);
  if (sha1)
    entries.push([
      'SHA1',
      sha1,
    ]);

  if (entries.length === 0) {
    return (
      <Text
        size="xs"
        c="dimmed"
      >
        —
      </Text>
    );
  }

  return (
    <Stack gap={2}>
      {entries.map(([label, value]) => (
        <HashLine
          key={label}
          label={label}
          value={value}
        />
      ))}
    </Stack>
  );
}

function FileName({ name }: { name: string }) {
  return (
    <Text
      size="xs"
      ff="monospace"
      truncate="end"
      title={name}
    >
      {name}
    </Text>
  );
}

function RomFilesTable({ roms }: { roms: DatRom[] }) {
  return (
    <Table
      verticalSpacing={6}
      horizontalSpacing="sm"
      fz="xs"
      layout="fixed"
    >
      <Table.Thead>
        <Table.Tr>
          <Table.Th>ROM</Table.Th>
          <Table.Th
            w={90}
            ta="right"
          >
            Size
          </Table.Th>
          <Table.Th w={340}>Hashes</Table.Th>
          <Table.Th w={100}>Status</Table.Th>
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {roms.map((rom) => (
          <Table.Tr key={rom.id}>
            <Table.Td>
              <FileName name={rom.name} />
            </Table.Td>
            <Table.Td ta="right">
              <Text
                size="xs"
                style={{
                  fontVariantNumeric: 'tabular-nums',
                }}
              >
                {rom.status === 'nodump' && Number(rom.size ?? 0) === 0
                  ? 'Unknown'
                  : formatBytes(rom.size || '0')}
              </Text>
            </Table.Td>
            <Table.Td>
              <HashList
                crc={rom.crc}
                md5={rom.md5}
                sha1={rom.sha1}
              />
            </Table.Td>
            <Table.Td>
              <StatusBadge status={rom.status} />
            </Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );
}

function DiskFilesTable({ disks }: { disks: DatDisk[] }) {
  return (
    <Table
      verticalSpacing={6}
      horizontalSpacing="sm"
      fz="xs"
      layout="fixed"
    >
      <Table.Thead>
        <Table.Tr>
          <Table.Th>Disk</Table.Th>
          <Table.Th w={340}>Hashes</Table.Th>
          <Table.Th w={100}>Status</Table.Th>
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {disks.map((disk) => (
          <Table.Tr key={disk.id}>
            <Table.Td>
              <FileName name={disk.name} />
            </Table.Td>
            <Table.Td>
              <HashList
                md5={disk.md5}
                sha1={disk.sha1}
              />
            </Table.Td>
            <Table.Td>
              <StatusBadge status={disk.status} />
            </Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );
}

/** The clone-of / requires (parent or BIOS) lineage a DAT records for a set. */
function GameLineage({ game }: { game: DatGame }) {
  const requires = game.romOf && game.romOf !== game.cloneOf ? game.romOf : null;

  if (!game.cloneOf && !requires) {
    if (game.description && game.description !== game.name) {
      return (
        <Text
          size="xs"
          c="dimmed"
          lineClamp={1}
        >
          {game.description}
        </Text>
      );
    }
    return null;
  }

  return (
    <Group
      gap="sm"
      wrap="nowrap"
    >
      {game.cloneOf && (
        <Text
          size="xs"
          c="dimmed"
          truncate="end"
        >
          clone of {game.cloneOf}
        </Text>
      )}
      {requires && (
        <Text
          size="xs"
          c="dimmed"
          truncate="end"
        >
          requires {requires}
        </Text>
      )}
    </Group>
  );
}

export interface DatGameRowProps {
  game: DatGame;
  /** Column count of the parent games table, so the detail row can span it. */
  columnCount: number;
}

export function DatEntryFiles({ game }: { game: DatGame }) {
  const roms = game.roms ?? [];
  const disks = game.disks ?? [];
  if (!roms.length && !disks.length) return <Text size="sm" c="dimmed">No files listed for this entry.</Text>;
  return <Table.ScrollContainer minWidth={700}><Stack gap="sm">{roms.length > 0 && <RomFilesTable roms={roms} />}{disks.length > 0 && <DiskFilesTable disks={disks} />}</Stack></Table.ScrollContainer>;
}

/**
 * One game entry in a DAT, expandable to reveal the ROM/disk files it requires —
 * the file-level detail that makes a DAT the source of truth for verification.
 */
export function DatGameRow({ game, columnCount }: DatGameRowProps) {
  const [expanded, setExpanded] = useState(false);
  const toggle = () => setExpanded((open) => !open);
  const roms = game.roms ?? [];
  const disks = game.disks ?? [];
  const fileCount = roms.length + disks.length;

  return (
    <>
      <Table.Tr
        onClick={toggle}
        onKeyDown={(event) => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            toggle();
          }
        }}
        tabIndex={0}
        aria-expanded={expanded}
        style={{
          cursor: 'pointer',
        }}
      >
        <Table.Td>
          <Group
            gap="xs"
            wrap="nowrap"
          >
            <IconChevronRight
              size={16}
              style={{
                flexShrink: 0,
                color: 'var(--mantine-color-dimmed)',
                transform: expanded ? 'rotate(90deg)' : 'none',
                transition: 'transform 150ms ease',
              }}
            />
            <Box
              style={{
                minWidth: 0,
              }}
            >
              <Group
                gap={6}
                wrap="nowrap"
              >
                <Text
                  size="sm"
                  fw={500}
                  truncate="end"
                  title={game.name}
                >
                  {game.name}
                </Text>
                {game.isBios && (
                  <Badge
                    size="xs"
                    variant="light"
                    color="grape"
                    radius="sm"
                  >
                    BIOS
                  </Badge>
                )}
              </Group>
              <GameLineage game={game} />
            </Box>
          </Group>
        </Table.Td>
        <Table.Td>
          <Badge
            variant="light"
            color="gray"
            size="sm"
            radius="sm"
          >
            {game.year || '—'}
          </Badge>
        </Table.Td>
        <Table.Td>
          <Text
            size="sm"
            c="dimmed"
            truncate="end"
          >
            {game.manufacturer || '—'}
          </Text>
        </Table.Td>
        <Table.Td ta="right">
          <Text
            size="sm"
            fw={500}
            style={{
              fontVariantNumeric: 'tabular-nums',
            }}
          >
            {fileCount.toLocaleString()}
          </Text>
        </Table.Td>
      </Table.Tr>

      {expanded && (
        <Table.Tr>
          <Table.Td
            colSpan={columnCount}
            style={{
              padding: 0,
            }}
          >
            <Box
              pl={40}
              pr="sm"
              py="sm"
              style={{
                backgroundColor: 'var(--mantine-color-default-hover)',
              }}
            >
              {fileCount === 0 ? (
                <Text
                  size="xs"
                  c="dimmed"
                >
                  No files listed for this entry.
                </Text>
              ) : (
                <Stack gap="sm">
                  {roms.length > 0 && <RomFilesTable roms={roms} />}
                  {disks.length > 0 && <DiskFilesTable disks={disks} />}
                </Stack>
              )}
            </Box>
          </Table.Td>
        </Table.Tr>
      )}
    </>
  );
}
