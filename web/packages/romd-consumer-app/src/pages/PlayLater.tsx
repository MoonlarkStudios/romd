import { Button, Group, SimpleGrid, Stack, Text, Title, VisuallyHidden } from '@mantine/core';
import { useFocusWithin, useHover, useMergedRef } from '@mantine/hooks';
import { IconArrowLeft } from '@tabler/icons-react';
import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import { PlayLaterButton } from '../components/library/PlayLaterButton';
import { PosterCard } from '../components/library/PosterCard';
import { StatusState } from '../components/status/StatusState';
import { useChangePlayLater, usePlayLaterTitles } from '../hooks/usePlayLater';

interface RecentRemoval {
  titleId: string;
  isCurrent: () => boolean;
  focusUndo: boolean;
}

const undoDuration = 10_000;

function UndoRemoval({
  removal,
  onExpire,
  onRestore,
}: {
  removal: RecentRemoval;
  onExpire: (removal: RecentRemoval) => void;
  onRestore: (removal: RecentRemoval, hadFocus: boolean) => void;
}) {
  const { change, pending } = useChangePlayLater(removal.titleId);
  const { hovered, ref: hoverRef } = useHover<HTMLDivElement>();
  const { focused, ref: focusRef } = useFocusWithin<HTMLDivElement>();
  const ref = useMergedRef(hoverRef, focusRef);
  const undo = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (removal.focusUndo) undo.current?.focus();
  }, [
    removal,
  ]);

  useEffect(() => {
    if (hovered || focused || pending) return;
    const timer = window.setTimeout(() => onExpire(removal), undoDuration);
    return () => window.clearTimeout(timer);
  }, [
    hovered,
    focused,
    pending,
    removal,
    onExpire,
  ]);

  return (
    <Group
      ref={ref}
      className="romd-play-later-undo"
      gap={6}
      wrap="nowrap"
    >
      <Text
        aria-hidden
        className="romd-metadata"
      >
        Removed from Play Later
      </Text>
      <Button
        ref={undo}
        variant="subtle"
        size="sm"
        mih={44}
        px="xs"
        color="mint"
        aria-busy={pending || undefined}
        aria-disabled={pending || undefined}
        data-disabled={pending || undefined}
        onClick={(event) => {
          if (!pending && removal.isCurrent()) {
            const keyboard = event.detail === 0;
            change(true, () =>
              onRestore(removal, keyboard && document.activeElement === undo.current),
            );
          }
        }}
      >
        Undo
      </Button>
    </Group>
  );
}

export function PlayLater() {
  const saved = usePlayLaterTitles();
  const [notice, setNotice] = useState<{
    removal: RecentRemoval;
    restored?: boolean;
    focusHeading?: boolean;
  } | null>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const currentNotice = notice?.removal.isCurrent() ? notice : null;
  const removal = currentNotice?.restored ? null : currentNotice?.removal;
  const titles = [
    ...new Map(
      saved.data?.pages.flatMap((page) =>
        page.items.map(
          (title) =>
            [
              title.id,
              title,
            ] as const,
        ),
      ),
    ).values(),
  ];

  const expireRemoval = useCallback((expired: RecentRemoval) => {
    setNotice((current) => (current?.removal === expired ? null : current));
  }, []);

  function reportRestoration(restoredRemoval: RecentRemoval, hadFocus: boolean) {
    setNotice((current) =>
      current?.removal === restoredRemoval
        ? {
            removal: restoredRemoval,
            restored: true,
            focusHeading: hadFocus,
          }
        : current,
    );
  }

  useEffect(() => {
    if (currentNotice?.focusHeading) heading.current?.focus();
  }, [
    currentNotice,
  ]);

  return (
    <Stack gap="var(--romd-page-gap)">
      <Stack
        gap={6}
        align="flex-start"
      >
        <Button
          variant="transparent"
          color="gray"
          size="sm"
          px={0}
          leftSection={<IconArrowLeft size={16} />}
          component={Link}
          to="/"
        >
          Home
        </Button>
        <Title
          ref={heading}
          tabIndex={-1}
          order={1}
          className="romd-page-heading"
        >
          Play Later
        </Title>
        {removal && (
          <UndoRemoval
            key={removal.titleId}
            removal={removal}
            onExpire={expireRemoval}
            onRestore={reportRestoration}
          />
        )}
        <VisuallyHidden role="status">
          {currentNotice && (
            <span key={currentNotice.removal.titleId}>
              {currentNotice.restored ? 'Restored to Play Later' : 'Removed from Play Later'}
            </span>
          )}
        </VisuallyHidden>
      </Stack>
      {saved.isError ? (
        <StatusState
          kind="error"
          message="Play Later could not be loaded."
          onRetry={() => void saved.refetch()}
          retrying={saved.isRefetching}
        />
      ) : saved.isLoading ? (
        <StatusState
          kind="loading"
          label="Loading Play Later"
        />
      ) : titles.length === 0 ? (
        <StatusState
          kind="empty"
          title="No games saved for later"
          message="Save games from Browse or a game page."
          action={
            <Button
              component={Link}
              to="/library"
              variant="light"
            >
              Browse games
            </Button>
          }
        />
      ) : (
        <SimpleGrid
          cols={{
            base: 2,
            xs: 3,
            md: 4,
            xl: 6,
          }}
          spacing="lg"
        >
          {titles.map((title) => (
            <PosterCard
              key={title.id}
              title={title}
              showPlayAction={false}
              showTitle
              playLaterAction={
                <PlayLaterButton
                  title={title}
                  membership={{
                    saved: true,
                  }}
                  compact
                  onRemoved={(result) =>
                    setNotice({
                      removal: {
                        titleId: title.id,
                        ...result,
                      },
                    })
                  }
                />
              }
            />
          ))}
        </SimpleGrid>
      )}
      {saved.hasNextPage && !saved.isError && (
        <Group justify="center">
          <Button
            onClick={() => void saved.fetchNextPage()}
            loading={saved.isFetchingNextPage}
            variant="light"
          >
            Load more
          </Button>
        </Group>
      )}
    </Stack>
  );
}
