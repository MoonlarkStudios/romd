import { ActionIcon, Badge, CopyButton, Group, Stack, Text, Tooltip } from '@mantine/core';
import type { DatFieldChange, DatFileChange } from '@romd/admin-api-client';
import { IconCheck, IconCopy } from '@tabler/icons-react';
import { formatBytes } from '../../utils/format';
import classes from './DatReview.module.css';

function FullValue({ value, label }: { value?: string | null; label?: string }) {
  return (
    <Group
      component="span"
      gap={6}
      wrap="nowrap"
    >
      <Text
        span
        size="sm"
        className={label ? classes.hash : classes.fieldValue}
      >
        {value ?? '—'}
      </Text>
      {label && value && (
        <CopyButton value={value}>
          {({ copied, copy }) => (
            <Tooltip label={copied ? 'Copied' : 'Copy checksum'}>
              <ActionIcon
                size="sm"
                variant="subtle"
                aria-label={copied ? 'Copied' : `Copy ${label}`}
                onClick={copy}
              >
                {copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
              </ActionIcon>
            </Tooltip>
          )}
        </CopyButton>
      )}
    </Group>
  );
}

export function DatReviewFields({ fields, file }: { fields: DatFieldChange[]; file?: boolean }) {
  return (
    <dl className={classes.fieldList}>
      {fields.map((field) => (
        <div
          key={field.field}
          className={classes.fieldRow}
        >
          <Text
            component="dt"
            size="sm"
            fw={600}
          >
            {field.field}
          </Text>
          <Text
            component="dd"
            size="sm"
            m={0}
            className={classes.fieldValue}
          >
            <Text
              span
              size="xs"
              c="dimmed"
              display="block"
            >
              Installed
            </Text>
            <FullValue
              value={field.before}
              label={
                file && Object.values(hashLabels).includes(field.field)
                  ? `installed ${field.field}`
                  : undefined
              }
            />
          </Text>
          <Text
            component="dd"
            size="sm"
            m={0}
            className={classes.fieldValue}
          >
            <Text
              span
              size="xs"
              c="dimmed"
              display="block"
            >
              Incoming
            </Text>
            <FullValue
              value={field.after}
              label={
                file && Object.values(hashLabels).includes(field.field)
                  ? `incoming ${field.field}`
                  : undefined
              }
            />
          </Text>
        </div>
      ))}
    </dl>
  );
}

const hashLabels: Record<string, string> = {
  Sha1: 'SHA-1',
  Crc: 'CRC32',
  Md5: 'MD5',
};

function HashValue({ hash }: { hash?: string | null }) {
  if (!hash)
    return (
      <Text
        span
        c="dimmed"
      >
        —
      </Text>
    );
  return (
    <code className={classes.hash}>
      {hash.length > 14 ? `${hash.slice(0, 8)}…${hash.slice(-4)}` : hash}
    </code>
  );
}

export function DatReviewFileComparison({ file }: { file: DatFileChange }) {
  const hashes = Object.keys(hashLabels)
    .map((algorithm) => file.fields.find((field) => field.field === algorithm))
    .filter((field) => field != null);
  const other = file.fields.filter((field) => !(field.field in hashLabels));
  const size = other.find((field) => field.field === 'Size');
  const bytes = size?.after ?? size?.before;
  const otherChanges = other.filter(
    (field) => field.before !== field.after && field.field !== 'Name',
  );
  return (
    <Stack
      component="li"
      gap="xs"
      className={classes.fileCard}
    >
      <Group
        gap="xs"
        wrap="wrap"
      >
        <Text
          fw={600}
          size="sm"
          className={classes.fieldValue}
        >
          {file.name}
        </Text>
        <Text
          size="xs"
          c="dimmed"
        >
          {file.kind === 'disk' ? 'Disc' : 'ROM'} · {file.change}
          {bytes ? ` · ${formatBytes(bytes)}` : ''}
        </Text>
      </Group>
      {hashes.length > 0 && (
        <ul
          className={classes.hashList}
          aria-label="Checksums"
        >
          {hashes.map(({ field, before, after }) => (
            <li
              key={field}
              className={classes.hashRow}
            >
              <Text
                span
                size="xs"
                fw={600}
              >
                {hashLabels[field]}
              </Text>
              <Badge
                size="sm"
                variant="light"
                color={before === after ? 'gray' : field === 'Sha1' ? 'yellow' : 'blue'}
              >
                {before === after ? 'Same' : !before ? 'Added' : !after ? 'Removed' : 'Changed'}
              </Badge>
              <div className={classes.hashValues}>
                <HashValue hash={before} />
                <Text
                  span
                  c="dimmed"
                  aria-hidden
                >
                  →
                </Text>
                <HashValue hash={after} />
              </div>
            </li>
          ))}
        </ul>
      )}
      {file.change === 'Changed' && otherChanges.length > 0 && (
        <Text
          size="xs"
          c="dimmed"
        >
          {otherChanges.map((field) => field.field).join(', ')} changed
        </Text>
      )}
      {file.impact && (
        <Stack gap={3}>
          {!file.impact.storedBefore && !file.impact.storedAfter && (
            <Text
              size="sm"
              c="dimmed"
            >
              No stored ROM matches this file.
            </Text>
          )}
          {file.impact.losesCatalogMatch && (
            <Text
              size="sm"
              className={classes.priorityText}
            >
              A stored ROM matches the installed file and will lose its match in this catalog.
            </Text>
          )}
          {file.impact.gainsCatalogMatch && (
            <Text
              size="sm"
              c="teal"
            >
              A stored ROM will gain a match in this catalog.
            </Text>
          )}
          {file.impact.storedBefore &&
            !file.impact.storedAfter &&
            !file.impact.losesCatalogMatch && (
              <Text
                size="sm"
                c="dimmed"
              >
                The stored ROM’s SHA-1 still appears elsewhere in the incoming DAT.
              </Text>
            )}
          {file.impact.storedAfter && !file.impact.gainsCatalogMatch && (
            <Text
              size="sm"
              c="dimmed"
            >
              A stored ROM matches the incoming SHA-1.
            </Text>
          )}
        </Stack>
      )}
      {file.kind === 'disk' && (
        <Text
          size="xs"
          c="dimmed"
        >
          Disc and CHD matching is outside this estimate.
        </Text>
      )}
      {(hashes.length > 0 || other.length > 0) && (
        <details className={classes.disclosure}>
          <summary>Full values</summary>
          <DatReviewFields
            file
            fields={file.fields.map((field) => ({
              ...field,
              field: hashLabels[field.field] ?? field.field,
            }))}
          />
        </details>
      )}
    </Stack>
  );
}
