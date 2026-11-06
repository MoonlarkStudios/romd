import {
  ActionIcon,
  Anchor,
  Badge,
  Button,
  Code,
  CopyButton,
  Group,
  Text,
  Tooltip,
} from '@mantine/core';
import type { TitleFileRequirement } from '@romd/admin-api-client';
import { IconCheck, IconCopy } from '@tabler/icons-react';
import { DataTable } from 'mantine-datatable';
import { useState } from 'react';
import { Link } from 'react-router';
import { RomDownloadDialog } from '../../components/RomDownloadDialog';
import { formatBytes } from '../../utils/format';
import classes from './ReleasesPanel.module.css';

interface FileRequirementsProps {
  files: TitleFileRequirement[];
}

function FileDownloadButton({ file }: { file: TitleFileRequirement }) {
  const [opened, setOpened] = useState(false);
  return (
    <>
      <Button
        size="xs"
        variant="subtle"
        aria-label={`Download for ${file.name}`}
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

function getStatusBadge(file: TitleFileRequirement) {
  if (file.isOwned) {
    return (
      <Badge
        color="green"
        size="sm"
      >
        Owned
      </Badge>
    );
  }

  if (file.status?.toLowerCase() === 'nodump') {
    return (
      <Badge
        color="gray"
        size="sm"
      >
        No Dump
      </Badge>
    );
  }

  return (
    <Badge
      color="red"
      size="sm"
    >
      Missing
    </Badge>
  );
}

export function FileRequirements({ files }: FileRequirementsProps) {
  if (files.length === 0) {
    return (
      <Text
        size="sm"
        c="dimmed"
      >
        No files in this release.
      </Text>
    );
  }

  return (
    <DataTable<TitleFileRequirement>
      minHeight={0}
      classNames={{
        header: classes.fileHeader,
      }}
      striped
      highlightOnHover
      records={[
        ...files,
      ].sort(
        (a, b) =>
          Number(!!a.isOwned || a.status?.toLowerCase() === 'nodump') -
          Number(!!b.isOwned || b.status?.toLowerCase() === 'nodump'),
      )}
      columns={[
        {
          accessor: 'name',
          title: 'File',
          width: 240,
          render: (file) =>
            file.romFileId ? (
              <Anchor
                component={Link}
                to={`/roms/${file.romFileId}?tab=all`}
                size="sm"
                style={{
                  overflowWrap: 'anywhere',
                }}
              >
                {file.name}
              </Anchor>
            ) : (
              <Text
                size="sm"
                fw={500}
                style={{
                  overflowWrap: 'anywhere',
                }}
              >
                {file.name}
              </Text>
            ),
        },
        {
          accessor: 'status',
          title: 'Status',
          width: 100,
          render: getStatusBadge,
        },
        {
          accessor: 'size',
          title: 'Size',
          width: 100,
          render: (file) => (
            <Text
              size="sm"
              c="dimmed"
            >
              {file.size != null ? formatBytes(file.size) : '—'}
            </Text>
          ),
        },
        {
          accessor: 'sha1',
          title: 'SHA-1',
          width: 180,
          render: (file) =>
            file.sha1 ? (
              <Group
                gap={4}
                wrap="nowrap"
              >
                <Tooltip label={file.sha1}>
                  <Code
                    fz="xs"
                    aria-label={`SHA-1: ${file.sha1}`}
                  >
                    {file.sha1.slice(0, 10)}…
                  </Code>
                </Tooltip>
                <CopyButton value={file.sha1}>
                  {({ copied, copy }) => (
                    <ActionIcon
                      variant="subtle"
                      color={copied ? 'teal' : 'gray'}
                      aria-label={`Copy SHA-1 for ${file.name}`}
                      onClick={copy}
                    >
                      {copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
                    </ActionIcon>
                  )}
                </CopyButton>
              </Group>
            ) : (
              <Text
                size="sm"
                c="dimmed"
              >
                —
              </Text>
            ),
        },
        {
          accessor: 'download',
          title: 'Download',
          width: 140,
          render: (file) => <FileDownloadButton file={file} />,
        },
      ]}
    />
  );
}
