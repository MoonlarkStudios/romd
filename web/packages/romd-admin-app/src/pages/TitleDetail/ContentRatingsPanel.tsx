import { Badge, Group, NativeSelect, Skeleton, Stack, Table, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type {
  RatingBoard,
  RatingBoardInfo,
  RatingCategoryInfo,
  TitleContentRating,
  TitleDetail,
} from '@romd/admin-api-client';
import { useMemo, useState } from 'react';
import { useRatingBoardCatalog, useSetTitleContentRating } from '../../hooks/api/useContentRating';
import { usePermissions } from '../../hooks/usePermissions';
import { ContentRatingRow } from './ContentRatingRow';
import {
  boardLabel,
  boardSortIndex,
  describeDesignation,
  formatContentRating,
  formatSourceLabel,
  ratingSourceColor,
} from './contentRatings';

interface ContentRatingsPanelProps {
  title: TitleDetail;
  titleId: string;
}

function categoryOptionLabel(board: RatingBoard, category: RatingCategoryInfo): string {
  const display = formatContentRating(board, category.code);
  const designation = describeDesignation(category.designation, category.minimumAge);
  return `${display} · ${designation}`;
}

/**
 * Per-board content ratings with provenance and an inline user re-rate control.
 * Reads effective ratings from the title (post-cascade) and writes user-layer overrides
 * that win the cascade for their board.
 */
export function ContentRatingsPanel({ title, titleId }: ContentRatingsPanelProps) {
  const { canManageTitles } = usePermissions();
  const { data: catalog, isLoading } = useRatingBoardCatalog();
  const setRating = useSetTitleContentRating();
  const [pendingBoard, setPendingBoard] = useState<RatingBoard | null>(null);

  const effectiveByBoard = useMemo(() => {
    const map = new Map<RatingBoard, TitleContentRating>();
    for (const rating of title.contentRatings ?? []) {
      map.set(rating.board, rating);
    }
    return map;
  }, [title.contentRatings]);

  const catalogByBoard = useMemo(() => {
    const map = new Map<RatingBoard, RatingBoardInfo>();
    for (const board of catalog?.boards ?? []) {
      map.set(board.board, board);
    }
    return map;
  }, [catalog]);

  // Display is driven by the title's own ratings (always shown) unioned with — for managers —
  // every board in the catalog so any board can be rated. The catalog is reference data for the
  // editor only; a failed/missing catalog never hides ratings the title already carries.
  const boardIds = useMemo(() => {
    const ids = new Set<RatingBoard>(effectiveByBoard.keys());
    if (canManageTitles) {
      for (const board of catalog?.boards ?? []) {
        ids.add(board.board);
      }
    }
    return [...ids].sort((left, right) => boardSortIndex(left) - boardSortIndex(right));
  }, [effectiveByBoard, catalog, canManageTitles]);

  if (isLoading && boardIds.length === 0) {
    return <Skeleton h={240} />;
  }

  if (boardIds.length === 0) {
    return (
      <Text size="sm" c="dimmed" ta="center" py="md">
        No content ratings for this title yet.
      </Text>
    );
  }

  const handleChange = async (board: RatingBoard, value: string) => {
    setPendingBoard(board);
    try {
      await setRating.mutateAsync({ titleId, board, code: value || null });
      notifications.show({
        title: 'Content rating updated',
        message: value
          ? `${formatContentRating(board, value)} saved as a user override.`
          : `${boardLabel(board)} override cleared.`,
        color: 'green',
      });
    } catch {
      notifications.show({
        title: 'Update failed',
        message: `Could not update the ${boardLabel(board)} rating.`,
        color: 'red',
      });
    } finally {
      setPendingBoard(null);
    }
  };

  return (
    <Stack gap="md">
      <ContentRatingRow ratings={title.contentRatings} />
      <Table verticalSpacing="sm" highlightOnHover>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Board</Table.Th>
            <Table.Th>Rating</Table.Th>
            <Table.Th>Classification</Table.Th>
            <Table.Th>Source</Table.Th>
            {canManageTitles && <Table.Th w={260}>User Override</Table.Th>}
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {boardIds.map((boardId) => {
            const boardInfo = catalogByBoard.get(boardId);
            const effective = effectiveByBoard.get(boardId);
            const userOverrideCode =
              effective && effective.sourceId.toLowerCase() === 'user' ? effective.code : '';
            const options = [
              { value: '', label: 'No user override' },
              ...(boardInfo?.categories ?? []).map((category) => ({
                value: category.code,
                label: categoryOptionLabel(boardId, category),
              })),
            ];

            return (
              <Table.Tr key={boardId}>
                <Table.Td>
                  <Text size="sm" fw={500}>
                    {boardLabel(boardId)}
                  </Text>
                </Table.Td>
                <Table.Td>
                  {effective ? (
                    <Stack gap={4}>
                      <Badge variant="light" color="orange">
                        {formatContentRating(boardId, effective.code)}
                      </Badge>
                      {effective.descriptors && effective.descriptors.length > 0 && (
                        <Group gap={4}>
                          {effective.descriptors.map((descriptor) => (
                            <Badge key={descriptor} size="xs" variant="outline" color="gray">
                              {descriptor}
                            </Badge>
                          ))}
                        </Group>
                      )}
                      {effective.synopsis && (
                        <Text size="xs" c="dimmed" lineClamp={2} style={{ maxWidth: '40ch' }}>
                          {effective.synopsis}
                        </Text>
                      )}
                    </Stack>
                  ) : (
                    <Text size="sm" c="dimmed">
                      Not rated
                    </Text>
                  )}
                </Table.Td>
                <Table.Td>
                  <Text size="sm" c={effective ? undefined : 'dimmed'}>
                    {effective
                      ? describeDesignation(effective.designation, effective.minimumAge)
                      : '-'}
                  </Text>
                </Table.Td>
                <Table.Td>
                  {effective ? (
                    <Badge size="sm" variant="dot" color={ratingSourceColor(effective.sourceId)}>
                      {formatSourceLabel(effective.sourceId)}
                    </Badge>
                  ) : (
                    <Text size="sm" c="dimmed">
                      -
                    </Text>
                  )}
                </Table.Td>
                {canManageTitles && (
                  <Table.Td>
                    {boardInfo ? (
                      <NativeSelect
                        aria-label={`${boardLabel(boardId)} user override`}
                        data={options}
                        value={userOverrideCode}
                        onChange={(event) => handleChange(boardId, event.currentTarget.value)}
                        disabled={setRating.isPending}
                      />
                    ) : (
                      <Text size="sm" c="dimmed">
                        -
                      </Text>
                    )}
                  </Table.Td>
                )}
              </Table.Tr>
            );
          })}
        </Table.Tbody>
      </Table>
      {canManageTitles && (
        <Text size="xs" c="dimmed">
          A user override wins the cascade for its board and is enforced by library parental
          controls. Clear it to fall back to provider data.
        </Text>
      )}
      {pendingBoard !== null && (
        <Text size="xs" c="dimmed">
          Saving {boardLabel(pendingBoard)} rating…
        </Text>
      )}
    </Stack>
  );
}
