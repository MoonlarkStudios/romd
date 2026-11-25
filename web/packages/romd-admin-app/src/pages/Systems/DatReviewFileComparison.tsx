import { ActionIcon, Badge, CopyButton, Group, Stack, Text, Tooltip } from '@mantine/core';
import type { DatFieldChange, DatFileChange } from '@romd/admin-api-client';
import { IconCheck, IconCopy } from '@tabler/icons-react';
import { formatBytes } from '../../utils/format';
import classes from './DatReview.module.css';

function FullValue({
  value,
  label,
  verbatim,
}: {
  value?: string | null;
  label?: string;
  verbatim?: boolean;
}) {
  const displayValue = verbatim && value && value !== value.trim() ? JSON.stringify(value) : value;

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
        style={
          verbatim
            ? {
                whiteSpace: 'break-spaces',
              }
            : undefined
        }
      >
        {displayValue === '' ? '(empty)' : (displayValue ?? '—')}
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
            {relationshipLabels.get(field.field) ?? field.field}
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
              verbatim={relationshipLabels.has(field.field)}
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
              verbatim={relationshipLabels.has(field.field)}
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

const relationshipLabels = new Map([
  [
    'DeclaredId',
    'DAT entry ID',
  ],
  [
    'CloneOf',
    'Clone of (name)',
  ],
  [
    'CloneOfId',
    'Clone of (ID)',
  ],
  [
    'RomOf',
    'ROM dependency',
  ],
  [
    'ParentName',
    'Resolved parent',
  ],
  [
    'ParentStatus',
    'Parent resolution',
  ],
]);

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

function MatchStatement({ file }: { file: DatFileChange }) {
  const impact = file.impact;
  if (!impact) return null;
  const statements: [
    string,
    'review' | 'gain' | 'quiet',
  ][] = [];
  if (impact.losesCatalogMatch)
    statements.push([
      'A stored ROM matches the installed file and loses its match in this catalog.',
      'review',
    ]);
  if (impact.gainsCatalogMatch)
    statements.push([
      'A stored ROM gains a match in this catalog.',
      'gain',
    ]);
  if (!statements.length)
    statements.push([
      impact.storedBefore && !impact.storedAfter
        ? 'The stored ROM keeps a match elsewhere in the incoming DAT.'
        : impact.storedAfter
          ? 'A stored ROM matches the incoming SHA-1.'
          : 'No stored ROM matches this file.',
      'quiet',
    ]);
  return (
    <>
      {statements.map(([text, tone]) => (
        <Text
          key={text}
          size="sm"
          c={tone === 'gain' ? 'teal' : tone === 'quiet' ? 'dimmed' : undefined}
          className={tone === 'review' ? classes.priorityText : undefined}
        >
          {text}
        </Text>
      ))}
    </>
  );
}

function FullValues({ file }: { file: DatFileChange }) {
  if (!file.fields.length) return null;
  return (
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
  // An added or removed file has no comparison to make: one line says what it is.
  const whole = file.change !== 'Changed';
  const sha1 = hashes.find((field) => field.field === 'Sha1');
  const wholeSha1 = sha1?.after ?? sha1?.before;
  return (
    <Stack
      component="li"
      gap="xs"
      className={classes.fileCard}
    >
      <div>
        <Text
          fw={600}
          size="sm"
          className={classes.fieldValue}
        >
          {file.name}
        </Text>
        <Group
          gap={6}
          wrap="wrap"
        >
          <Text
            size="xs"
            c="dimmed"
          >
            {file.kind === 'disk' ? 'Disc' : 'ROM'} · {file.change.toLowerCase()}
            {bytes ? ` · ${formatBytes(bytes)}` : ''}
          </Text>
          {whole && wholeSha1 && (
            <Group
              component="span"
              gap={4}
              wrap="nowrap"
            >
              <Text
                span
                size="xs"
                c="dimmed"
              >
                SHA-1
              </Text>
              <HashValue hash={wholeSha1} />
            </Group>
          )}
        </Group>
      </div>
      {!whole && hashes.length > 0 && (
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
                {before !== after && (
                  <>
                    <Text
                      span
                      c="dimmed"
                      aria-hidden
                    >
                      →
                    </Text>
                    <HashValue hash={after} />
                  </>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}
      {!whole && otherChanges.length > 0 && (
        <Text
          size="xs"
          c="dimmed"
        >
          {otherChanges.map((field) => field.field).join(', ')} changed
        </Text>
      )}
      <MatchStatement file={file} />
      {file.kind === 'disk' && (
        <Text
          size="xs"
          c="dimmed"
        >
          Disc and CHD matching is outside this estimate.
        </Text>
      )}
      <FullValues file={file} />
    </Stack>
  );
}
