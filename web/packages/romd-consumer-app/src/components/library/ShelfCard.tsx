import { Box, Stack, Text } from '@mantine/core';
import type { ConsumerCollectionDto } from '@romd/consumer-api-client';
import { IconFolder } from '@tabler/icons-react';
import { Link } from 'react-router';
import { formatCount } from '../../utils/format';

interface ShelfCardProps {
  collection: ConsumerCollectionDto;
}

export function ShelfCard({ collection }: ShelfCardProps) {
  const art = collection.heroUrl ?? collection.coverUrl ?? null;

  return (
    <Box
      component={Link}
      to={`/collections/${collection.id}`}
      className="romd-poster"
      aria-label={collection.name}
      style={{
        display: 'block',
        height: 150,
        color: 'inherit',
        textDecoration: 'none',
        background: art
          ? `url(${art}) center / cover`
          : 'var(--romd-brand-gradient)',
      }}
    >
      {!art && (
        <Box
          style={{
            position: 'absolute',
            inset: 0,
            display: 'grid',
            placeItems: 'center',
            color: 'rgba(255, 255, 255, 0.16)',
          }}
        >
          <IconFolder
            size={44}
            stroke={1.4}
          />
        </Box>
      )}

      <Box
        style={{
          position: 'absolute',
          inset: 0,
          background: 'linear-gradient(180deg, transparent 35%, rgba(0, 0, 0, 0.82) 100%)',
        }}
      />

      <Stack
        gap={2}
        style={{ position: 'absolute', left: 14, right: 14, bottom: 12 }}
      >
        <Text
          fw={700}
          fz={15}
          lineClamp={1}
        >
          {collection.name}
        </Text>
        <Text
          fz={12}
          c="dimmed"
        >
          {formatCount(collection.itemCount)} games
        </Text>
      </Stack>
    </Box>
  );
}
