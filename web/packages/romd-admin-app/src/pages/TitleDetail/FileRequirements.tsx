import { Anchor, Badge, Code, Table, Text } from '@mantine/core';
import type { TitleFileRequirement } from '@romd/admin-api-client';
import { Link } from 'react-router';
import { formatBytes } from '../../utils/format';

interface FileRequirementsProps {
  files: TitleFileRequirement[];
}

function getStatusBadge(file: TitleFileRequirement) {
  if (file.isOwned) {
    return (
      <Badge color="green" size="sm">
        Owned
      </Badge>
    );
  }

  if (file.status === 'nodump') {
    return (
      <Badge color="gray" size="sm">
        No Dump
      </Badge>
    );
  }

  return (
    <Badge color="red" size="sm">
      Missing
    </Badge>
  );
}

export function FileRequirements({ files }: FileRequirementsProps) {
  if (files.length === 0) {
    return (
      <Text size="sm" c="dimmed">
        No files in this release.
      </Text>
    );
  }

  return (
    <Table.ScrollContainer minWidth={540}>
    <Table striped highlightOnHover style={{ tableLayout: 'fixed' }}>
      <Table.Thead>
        <Table.Tr>
          <Table.Th>File</Table.Th>
          <Table.Th w={80}>Size</Table.Th>
          <Table.Th w={140}>SHA1</Table.Th>
          <Table.Th w={100}>Status</Table.Th>
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {files.map((file) => (
          <Table.Tr key={file.id}>
            <Table.Td>
              {file.romFileId ? <Anchor component={Link} to={`/roms/${file.romFileId}?tab=all`} size="sm" style={{ overflowWrap: 'anywhere' }}>{file.name}</Anchor> : <Text size="sm" fw={500} style={{ overflowWrap: 'anywhere' }}>{file.name}</Text>}
            </Table.Td>
            <Table.Td>
              <Text size="sm" c="dimmed">
                {file.size ? formatBytes(file.size) : '-'}
              </Text>
            </Table.Td>
            <Table.Td>
              {file.sha1 ? (
                <Code fz="xs">{file.sha1.slice(0, 12)}...</Code>
              ) : (
                <Text size="sm" c="dimmed">
                  -
                </Text>
              )}
            </Table.Td>
            <Table.Td>{getStatusBadge(file)}</Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
    </Table.ScrollContainer>
  );
}
