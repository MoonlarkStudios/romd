import {
  Alert,
  Button,
  Group,
  Skeleton,
  Stack,
  Table,
  Text,
  Title,
  UnstyledButton,
} from '@mantine/core';
import type { RatingBoard } from '@romd/admin-api-client';
import { RatingMark, useReferenceCatalog } from '@romd/consumer-ui';
import '@romd/consumer-ui/styles.css';
import { IconPlus } from '@tabler/icons-react';
import { useEffect, useEffectEvent, useRef, useState } from 'react';
import { useDiscardEpoch, useDraftGuard } from '../../components/Administration/DraftGuard';
import { ratingPresentation } from '../../components/Title/ratingPresentation';
import { InspectorLayout } from '../../components/Workspace/InspectorLayout';
import {
  useInspectorSelection,
  useInspectorSize,
} from '../../components/Workspace/useInspectorSelection';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useRatingBoardCatalog, useSetTitleContentRating } from '../../hooks/api/useContentRating';
import type { TitleDetailView as TitleDetail } from '../../hooks/api/useTitleDetail';
import { usePermissions } from '../../hooks/usePermissions';
import { ContentRatingEditor } from './ContentRatingEditor';
import classes from './ContentRatingsPanel.module.css';
import {
  boardLabel,
  boardRegion,
  boardSortIndex,
  describeDesignation,
  ratingSourceLabel,
} from './contentRatings';

interface ContentRatingsPanelProps {
  title: TitleDetail;
  titleId: string;
}

const inspectorId = 'age-rating-inspector';
const addRating = 'new';

export function ContentRatingsPanel({ title, titleId }: ContentRatingsPanelProps) {
  const { canManageTitles } = usePermissions();
  const catalog = useRatingBoardCatalog();
  const reference = useReferenceCatalog();
  const mutation = useSetTitleContentRating();
  const { ref, wide } = useInspectorSize();
  const discardEpoch = useDiscardEpoch();
  const { discardThen } = useDraftGuard();
  const addButton = useRef<HTMLButtonElement>(null);
  const [removedBoard, setRemovedBoard] = useState(false);
  const [addedBoard, setAddedBoard] = useState<RatingBoard>();
  const rows = [
    ...(title.contentRatings ?? []),
  ].sort((left, right) => boardSortIndex(left.board) - boardSortIndex(right.board));
  const availableBoards = (catalog.data ?? []).filter(
    ({ board, categories }) => categories.length && !rows.some((row) => row.board === board),
  );
  const ids = rows.map((rating) => rating.board);

  const { selectedId, select, close, rowProps, onListKeyDown } = useInspectorSelection({
    param: 'board',
    ids,
    allowUnlisted: true,
    panelId: inspectorId,
    canChange: () => !mutation.isPending,
    beforeChange: (action) => void discardThen(action),
  });
  const existing = rows.find((rating) => rating.board === selectedId);
  const adding = canManageTitles && selectedId === addRating;

  // The accepted write updates the table. Move the selection once the new row exists and the draft
  // has unregistered, so the transition does not trigger the unsaved-changes prompt.
  const selectAdded = useEffectEvent((board: RatingBoard) => select(board));
  const addedRowReady = rows.some((rating) => rating.board === addedBoard);
  useEffect(() => {
    if (!addedBoard || !addedRowReady || mutation.isPending || !adding) return;
    selectAdded(addedBoard);
    setAddedBoard(undefined);
  }, [
    addedBoard,
    addedRowReady,
    adding,
    mutation.isPending,
  ]);

  const closeRemoved = useEffectEvent(() => {
    close();
    addButton.current?.focus();
  });
  useEffect(() => {
    if (!removedBoard || mutation.isPending) return;
    closeRemoved();
    setRemovedBoard(false);
  }, [
    removedBoard,
    mutation.isPending,
  ]);

  const inspector = (existing || adding) && (
    <ContentRatingEditor
      key={`${titleId}:${selectedId}:${discardEpoch}`}
      id={inspectorId}
      titleId={titleId}
      revision={title.revision}
      rating={existing}
      boards={adding ? availableBoards : (catalog.data ?? [])}
      catalogLoading={catalog.isLoading}
      catalogError={catalog.isError}
      canManage={canManageTitles}
      saving={mutation.isPending}
      write={mutation.mutateAsync}
      onAdded={setAddedBoard}
      onRemoved={() => setRemovedBoard(true)}
      onClose={close}
    />
  );

  return (
    <div ref={ref}>
      <InspectorLayout
        wide={wide}
        narrow="stack"
        width="23rem"
        inspector={inspector || undefined}
      >
        <section
          aria-labelledby="age-ratings-heading"
          className={`${workspace.panel} ${classes.list}`}
        >
          <Group
            justify="space-between"
            gap="sm"
            className={classes.listHeader}
          >
            <Title
              id="age-ratings-heading"
              order={2}
              className={workspace.sectionHeading}
            >
              Age ratings
            </Title>
            {canManageTitles && availableBoards.length > 0 && (
              <Button
                {...workspaceActionProps}
                leftSection={<IconPlus size={16} />}
                ref={addButton}
                aria-controls={adding ? inspectorId : undefined}
                aria-expanded={adding}
                disabled={mutation.isPending}
                onClick={(event) => select(addRating, event.currentTarget)}
              >
                Add rating
              </Button>
            )}
          </Group>
          {canManageTitles && catalog.isError && (
            <Alert
              color="yellow"
              title="Rating categories are unavailable"
              mx="md"
              mb="md"
            >
              <Button
                size="compact-sm"
                variant="subtle"
                onClick={() => void catalog.refetch()}
              >
                Retry categories
              </Button>
            </Alert>
          )}
          {rows.length ? (
            <Table
              aria-labelledby="age-ratings-heading"
              className={classes.table}
              verticalSpacing="sm"
              horizontalSpacing="md"
              onKeyDown={onListKeyDown}
            >
              <Table.Thead>
                <Table.Tr>
                  <Table.Th scope="col">Board</Table.Th>
                  <Table.Th scope="col">Rating</Table.Th>
                  <Table.Th
                    scope="col"
                    className={classes.regionColumn}
                  >
                    Region
                  </Table.Th>
                  <Table.Th
                    scope="col"
                    className={classes.descriptorsColumn}
                  >
                    Descriptors
                  </Table.Th>
                  <Table.Th scope="col">Source</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {rows.map((rating) => {
                  const label = boardLabel(rating.board);
                  const region = boardRegion(rating.board);
                  return (
                    <Table.Tr
                      key={rating.board}
                      className={classes.row}
                      {...rowProps(rating.board)}
                    >
                      <Table.Th scope="row">
                        <UnstyledButton
                          data-inspector-trigger
                          aria-label={`${label} rating`}
                          aria-expanded={selectedId === rating.board}
                          aria-controls={selectedId === rating.board ? inspectorId : undefined}
                          className={classes.boardButton}
                          disabled={mutation.isPending}
                          onClick={(event) => select(rating.board, event.currentTarget)}
                        >
                          <Text
                            component="span"
                            className="romd-card-title"
                            size="sm"
                          >
                            {label}
                          </Text>
                          <Text
                            component="span"
                            size="xs"
                            c="dimmed"
                            className={classes.compactRegion}
                          >
                            {region}
                          </Text>
                        </UnstyledButton>
                      </Table.Th>
                      <Table.Td>
                        <Stack
                          gap={4}
                          align="flex-start"
                        >
                          <RatingMark
                            {...ratingPresentation(reference, rating)}
                            size={34}
                          />
                          <Text
                            size="xs"
                            c="dimmed"
                          >
                            {describeDesignation(rating.designation, rating.minimumAge)}
                          </Text>
                        </Stack>
                      </Table.Td>
                      <Table.Td className={classes.regionColumn}>
                        <Text size="sm">{region}</Text>
                      </Table.Td>
                      <Table.Td className={classes.descriptorsColumn}>
                        <Text
                          size="sm"
                          lineClamp={2}
                        >
                          {rating.descriptors?.join(', ') || '—'}
                        </Text>
                      </Table.Td>
                      <Table.Td>
                        <Text
                          size="sm"
                          c="dimmed"
                        >
                          {ratingSourceLabel(rating.sourceId)}
                        </Text>
                      </Table.Td>
                    </Table.Tr>
                  );
                })}
              </Table.Tbody>
            </Table>
          ) : catalog.isLoading && canManageTitles ? (
            <Skeleton
              h={56}
              m="md"
            />
          ) : (
            <Text
              px="md"
              pb="lg"
              size="sm"
              c="dimmed"
            >
              This title has no age ratings yet.
            </Text>
          )}
        </section>
      </InspectorLayout>
    </div>
  );
}
