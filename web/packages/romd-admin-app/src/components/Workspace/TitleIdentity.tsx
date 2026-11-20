import { Badge, Group, Stack, Text, Title } from '@mantine/core';
import type { ReactNode } from 'react';
import type { TitleDetailView } from '../../hooks/api/useTitleDetail';
import { ArtworkFrame } from '../ArtworkFrame';
import classes from './TitleIdentity.module.css';

interface TitleIdentityProps {
  title: TitleDetailView;
  platform?: string;
  size?: 'compact' | 'hero';
  showName?: boolean;
  nameAction?: ReactNode;
  children?: ReactNode;
}

export function TitleIdentity({
  title,
  platform,
  size = 'compact',
  showName = true,
  nameAction,
  children,
}: TitleIdentityProps) {
  const poster = title.artwork?.find((item) => item.role === 'Poster' && item.url);
  const covers = title.media.filter((item) => item.type?.toLowerCase() === 'cover');
  const cover = covers.find((item) => item.isPrimary) ?? covers[0];

  return (
    <div
      className={classes.identity}
      data-size={size}
    >
      <div className={classes.poster}>
        <ArtworkFrame
          artworkRole="Poster"
          src={poster?.url ?? cover?.url}
          title={title.name}
          fit={poster?.fit === 'Cover' ? 'Cover' : 'Contain'}
          loading={size === 'compact' ? 'lazy' : undefined}
        />
      </div>
      <Stack
        gap={size === 'hero' ? 'sm' : 'xs'}
        className={classes.details}
      >
        <Text
          size="sm"
          c={size === 'compact' ? 'dimmed' : undefined}
        >
          {platform || title.systemKey}
        </Text>
        {showName && (
          <Group
            gap="xs"
            wrap="nowrap"
            align="start"
          >
            <Title
              order={size === 'hero' ? 1 : 2}
              className={size === 'hero' ? classes.title : 'romd-section-heading'}
            >
              {title.name}
            </Title>
            {nameAction}
          </Group>
        )}
        {(title.releaseDate || title.genre) && (
          <Group gap="xs">
            {title.releaseDate && <Text size="sm">{title.releaseDate.slice(0, 4)}</Text>}
            {title.genre && (
              <Badge
                variant="light"
                color="gray"
              >
                {title.genre}
              </Badge>
            )}
          </Group>
        )}
        {children}
      </Stack>
    </div>
  );
}
