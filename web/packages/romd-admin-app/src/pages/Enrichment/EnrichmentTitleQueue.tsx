import { Button, Group, Skeleton, Stack, Text, UnstyledButton } from '@mantine/core';
import { useState } from 'react';
import { outstandingLabel } from '../../components/Title/enrichmentPresentation';
import { TitleStatusChips, type TitleStatusFlags } from '../../components/Title/TitleStatusChips';
import classes from './EnrichmentWorkspace.module.css';

export interface EnrichmentQueueItem {
  id: string;
  name: string;
  platform: string;
  platformTitle?: string;
  thumbnail?: string | null;
  reason?: string;
  outstandingCount?: number;
  reviewing: boolean;
  /** Kept in place after leaving the view until the curator navigates; not counted as part of the view. */
  departed?: boolean;
  flags: TitleStatusFlags;
}

export function EnrichmentTitleQueue({
  items,
  selectedId,
  pending,
  hasMore,
  loadingMore,
  refreshing,
  onSelect,
  onMore,
}: {
  items: EnrichmentQueueItem[];
  selectedId?: string;
  pending: boolean;
  hasMore: boolean;
  loadingMore: boolean;
  refreshing: boolean;
  onSelect: (id: string) => void;
  onMore: () => void;
}) {
  const listed = items.filter((item) => !item.departed).length;
  return (
    <Stack
      className={classes.queue}
      gap="xs"
      component="nav"
      aria-label="Enrichment title queue"
    >
      <Text
        size="xs"
        c="dimmed"
      >
        {pending
          ? 'Loading titles…'
          : `${listed}${hasMore ? '+' : ''} ${listed === 1 && !hasMore ? 'title' : 'titles'}`}
      </Text>
      {pending && (
        <>
          <Skeleton h={80} />
          <Skeleton h={80} />
          <Skeleton h={80} />
        </>
      )}
      {items.map((item) => (
        <UnstyledButton
          className={classes.titleButton}
          key={item.id}
          aria-current={item.id === selectedId}
          onClick={() => onSelect(item.id)}
        >
          <QueueThumbnail
            key={item.thumbnail}
            name={item.name}
            src={item.thumbnail}
          />
          <Stack
            gap={3}
            className={classes.queueText}
          >
            <Text
              size="sm"
              fw={600}
              lineClamp={2}
            >
              {item.name}
            </Text>
            <Group gap="xs">
              <Text
                size="xs"
                c="dimmed"
                lineClamp={1}
                title={item.platformTitle ?? item.platform}
                aria-label={item.platformTitle ?? item.platform}
              >
                {item.platform}
              </Text>
              {!!item.outstandingCount && (
                <Text
                  size="xs"
                  c="dimmed"
                >
                  {outstandingLabel(item.outstandingCount)}
                </Text>
              )}
            </Group>
            {item.reason && (
              <Text
                size="xs"
                c={item.reviewing ? 'orange' : 'dimmed'}
                lineClamp={2}
              >
                {item.reason}
              </Text>
            )}
            <TitleStatusChips
              flags={item.flags}
              label={`${item.name} enrichment status`}
            />
          </Stack>
        </UnstyledButton>
      ))}
      {hasMore && (
        <Button
          variant="subtle"
          loading={loadingMore}
          disabled={refreshing}
          onClick={onMore}
        >
          Load more titles
        </Button>
      )}
    </Stack>
  );
}

function QueueThumbnail({ name, src }: { name: string; src?: string | null }) {
  const [failed, setFailed] = useState(false);
  return src && !failed ? (
    <img
      className={classes.poster}
      src={src}
      alt=""
      loading="lazy"
      onError={() => setFailed(true)}
    />
  ) : (
    <span
      className={classes.poster}
      aria-hidden="true"
    >
      {name.slice(0, 1).toUpperCase()}
    </span>
  );
}
