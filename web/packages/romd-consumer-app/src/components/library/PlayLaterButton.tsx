import { ActionIcon, Button, Tooltip } from '@mantine/core';
import type { ConsumerTitleCard } from '@romd/consumer-api-client';
import { IconBookmark, IconBookmarkFilled, IconRefresh } from '@tabler/icons-react';
import { type PlayLaterMembership, useChangePlayLater } from '../../hooks/usePlayLater';

interface PlayLaterButtonProps {
  title: Pick<ConsumerTitleCard, 'id' | 'name'>;
  membership?: PlayLaterMembership;
  compact?: boolean;
}

export function PlayLaterButton({ title, membership, compact }: PlayLaterButtonProps) {
  const { available, pending, change } = useChangePlayLater(title.id);
  if (!available) return null;

  const { saved, retry } = membership ?? {};
  const loading = saved === undefined && !retry;
  const disabled = pending || loading;
  const label = retry
    ? 'Retry Play Later'
    : loading
      ? 'Checking Play Later'
      : saved
        ? 'Remove from Play Later'
        : 'Add to Play Later';
  const icon = retry ? (
    <IconRefresh
      size={18}
      aria-hidden
    />
  ) : saved ? (
    <IconBookmarkFilled
      size={18}
      aria-hidden
    />
  ) : (
    <IconBookmark
      size={18}
      aria-hidden
    />
  );
  const handleClick = () => {
    if (disabled) return;
    if (retry) retry();
    else change(!saved);
  };
  const control = {
    type: 'button' as const,
    'aria-busy': disabled || undefined,
    'aria-disabled': disabled || undefined,
    'data-disabled': disabled || undefined,
    onClick: handleClick,
  };
  if (!compact) {
    const caption = saved ? 'Saved' : 'Play Later';
    return (
      <Button
        size="md"
        variant="subtle"
        color="gray"
        aria-label={saved && !retry ? 'Saved, remove from Play Later' : undefined}
        leftSection={icon}
        {...control}
      >
        {retry || loading ? label : caption}
      </Button>
    );
  }
  return (
    <Tooltip
      label={label}
      events={{
        hover: true,
        focus: true,
        touch: false,
      }}
    >
      <ActionIcon
        className="romd-play-later-icon"
        variant="transparent"
        radius="xl"
        size={44}
        aria-label={`${retry || loading ? label : 'Play Later'} for ${title.name}`}
        aria-pressed={retry ? undefined : saved}
        {...control}
      >
        {icon}
      </ActionIcon>
    </Tooltip>
  );
}
