import { Anchor, Badge, Button, Group, Stack, Text } from '@mantine/core';
import type { TitleFileRequirement } from '@romd/admin-api-client';
import { IconChevronRight, IconDownload } from '@tabler/icons-react';
import { useState } from 'react';
import { Link } from 'react-router';
import { HashRow } from '../../components/HashRow';
import { RomDownloadDialog } from '../../components/RomDownloadDialog';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { formatBytes } from '../../utils/format';
import classes from './ReleasesPanel.module.css';

function FileDownloadButton({ file }: { file: TitleFileRequirement }) {
  const [opened, setOpened] = useState(false);
  return (
    <>
      <Button
        {...workspaceActionProps}
        size="compact-sm"
        variant="subtle"
        aria-label={`Download for ${file.name}`}
        leftSection={<IconDownload size={14} />}
        disabled={!file.isOwned || !file.romFileId}
        onClick={() => setOpened(true)}
      >
        Download
      </Button>
      {opened && file.romFileId && (
        <RomDownloadDialog
          romId={file.romFileId}
          onClose={() => setOpened(false)}
        />
      )}
    </>
  );
}

function FileStatusBadge({ file }: { file: TitleFileRequirement }) {
  if (file.isOwned)
    return (
      <Badge
        color="green"
        size="xs"
        variant="light"
      >
        Owned
      </Badge>
    );
  if (file.status?.toLowerCase() === 'nodump')
    return (
      <Badge
        color="gray"
        size="xs"
        variant="light"
      >
        No dump
      </Badge>
    );
  return (
    <Badge
      color="red"
      size="xs"
      variant="light"
    >
      Missing
    </Badge>
  );
}

const sortRank = (file: TitleFileRequirement) =>
  Number(!!file.isOwned || file.status?.toLowerCase() === 'nodump');

export function FileRequirements({ files }: { files: TitleFileRequirement[] }) {
  if (files.length === 0)
    return (
      <Text
        size="sm"
        c="dimmed"
      >
        No files in this release.
      </Text>
    );

  return (
    <ul className={classes.fileList}>
      {[
        ...files,
      ]
        .sort((a, b) => sortRank(a) - sortRank(b))
        .map((file) => (
          <li
            key={file.id}
            className={classes.file}
          >
            <div className={classes.fileHeader}>
              {file.romFileId ? (
                <Anchor
                  component={Link}
                  to={`/roms/${file.romFileId}?tab=all`}
                  size="sm"
                >
                  {file.name}
                </Anchor>
              ) : (
                <Text
                  size="sm"
                  fw={500}
                >
                  {file.name}
                </Text>
              )}
              <FileStatusBadge file={file} />
            </div>
            <Group
              gap="xs"
              align="center"
              justify="space-between"
            >
              <Text
                size="xs"
                c="dimmed"
              >
                {file.size != null ? formatBytes(file.size) : 'Size unavailable'}
              </Text>
              <FileDownloadButton file={file} />
            </Group>
            {(file.sha1 || file.md5 || file.crc) && (
              <details className={classes.hashDisclosure}>
                <summary aria-label={`Hashes for ${file.name}`}>
                  <IconChevronRight
                    size={14}
                    aria-hidden
                  />
                  Hashes
                </summary>
                <Stack
                  gap={4}
                  mt="xs"
                >
                  {file.sha1 && (
                    <HashRow
                      label="SHA-1"
                      value={file.sha1}
                    />
                  )}
                  {file.md5 && (
                    <HashRow
                      label="MD5"
                      value={file.md5}
                    />
                  )}
                  {file.crc && (
                    <HashRow
                      label="CRC"
                      value={file.crc}
                    />
                  )}
                </Stack>
              </details>
            )}
          </li>
        ))}
    </ul>
  );
}
