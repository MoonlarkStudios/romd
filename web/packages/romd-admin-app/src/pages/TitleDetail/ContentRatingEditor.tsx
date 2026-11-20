import {
  Alert,
  Button,
  Group,
  Modal,
  NativeSelect,
  Stack,
  TagsInput,
  Text,
  Textarea,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { RatingBoard, TitleContentRating, TitleDetail } from '@romd/admin-api-client';
import { RatingMark, useReferenceCatalog } from '@romd/consumer-ui';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useDraft } from '../../components/Administration/DraftGuard';
import { ratingPresentation } from '../../components/Title/ratingPresentation';
import { TitleConflictAlert } from '../../components/Title/TitleConflictAlert';
import { EditorFooter } from '../../components/Workspace/EditorFooter';
import { InspectorPanel } from '../../components/Workspace/InspectorPanel';
import { SourceMarker } from '../../components/Workspace/SourceMarker';
import { TitleConflictError } from '../../hooks/api/titleConflict';
import type { RatingBoardOption, SetContentRatingParams } from '../../hooks/api/useContentRating';
import { titleDetailQuery } from '../../hooks/api/useTitleDetail';
import {
  boardLabel,
  boardRegion,
  describeDesignation,
  formatContentRating,
  isManualRating,
  ratingSourceLabel,
} from './contentRatings';

interface RatingValues {
  code: string;
  descriptors: string[];
  descriptorInput: string;
  synopsis: string;
  forceManual: boolean;
}

interface RatingDraft {
  revision: string;
  baseline: RatingValues;
  values: RatingValues;
}

interface ContentRatingEditorProps {
  id: string;
  titleId: string;
  revision: string;
  rating?: TitleContentRating;
  boards: readonly RatingBoardOption[];
  catalogLoading: boolean;
  catalogError: boolean;
  canManage: boolean;
  saving: boolean;
  write: (values: SetContentRatingParams) => Promise<TitleDetail>;
  onAdded: (board: RatingBoard) => void;
  onRemoved: () => void;
  onClose: () => void;
}

function draftMatchesBaseline(values: RatingValues, baseline: RatingValues) {
  return (
    values.code === baseline.code &&
    values.synopsis === baseline.synopsis &&
    values.descriptorInput === baseline.descriptorInput &&
    values.forceManual === baseline.forceManual &&
    values.descriptors.length === baseline.descriptors.length &&
    values.descriptors.every((descriptor, index) => descriptor === baseline.descriptors[index])
  );
}

export function ContentRatingEditor({
  id,
  titleId,
  revision,
  rating,
  boards,
  catalogLoading,
  catalogError,
  canManage,
  saving,
  write,
  onAdded,
  onRemoved,
  onClose,
}: ContentRatingEditorProps) {
  const reference = useReferenceCatalog();
  const cache = useQueryClient();
  const [newBoard, setNewBoard] = useState<RatingBoard | ''>('');
  const [draft, setDraft] = useState<RatingDraft>();
  const [clearRevision, setClearRevision] = useState<string>();
  const [conflict, setConflict] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string>();

  const board = rating?.board ?? (boards.some((item) => item.board === newBoard) ? newBoard : '');
  const boardInfo = boards.find((item) => item.board === board);
  const manual = isManualRating(rating?.sourceId);
  const baseline: RatingValues = {
    code: rating?.code ?? '',
    descriptors: rating?.descriptors ?? [],
    descriptorInput: '',
    synopsis: rating?.synopsis ?? '',
    forceManual: false,
  };
  const values = draft?.values ?? baseline;
  const dirty = draft !== undefined;
  const categories = boardInfo?.categories ?? [];
  const validCode = categories.some((category) => category.code === values.code);
  const canEdit = canManage && categories.length > 0;

  useDraft(dirty, 'Your age rating draft is unsaved.');

  const change = (patch: Partial<RatingValues>) => {
    setErrorMessage(undefined);
    setDraft((current) => {
      const original = current?.baseline ?? baseline;
      const next = {
        ...(current?.values ?? original),
        ...patch,
      };
      return draftMatchesBaseline(next, original)
        ? undefined
        : {
            revision: current?.revision ?? revision,
            baseline: original,
            values: next,
          };
    });
  };

  const discard = () => {
    setDraft(undefined);
    setConflict(false);
    setErrorMessage(undefined);
  };

  const selectBoard = (value: string) => {
    const next = boards.find((item) => item.board === value)?.board ?? '';
    setNewBoard(next);
    change({
      code: '',
    });
  };

  const save = async (code: string | null, basedOnRevision: string) => {
    if (!board || saving || (code !== null && !validCode)) return;
    setErrorMessage(undefined);
    const descriptor = values.descriptorInput.trim();
    const descriptors = [
      ...values.descriptors,
    ];
    if (
      descriptor &&
      !descriptors.some((value) => value.toLowerCase() === descriptor.toLowerCase())
    )
      descriptors.push(descriptor);

    try {
      const accepted = await write({
        titleId,
        revision: basedOnRevision,
        board,
        code,
        ...(code && descriptors.length
          ? {
              descriptors,
            }
          : {}),
        ...(code && values.synopsis.trim()
          ? {
              synopsis: values.synopsis.trim(),
            }
          : {}),
      });
      discard();
      notifications.show({
        title: code ? 'Age rating saved' : 'Override cleared',
        message: code ? formatContentRating(board, code) : boardLabel(board),
        color: 'green',
      });
      const boardStillRated = accepted.contentRatings?.some((item) => item.board === board);
      if (!rating) onAdded(board);
      else if (!code && !boardStillRated) onRemoved();
    } catch (error) {
      if (error instanceof TitleConflictError) setConflict(true);
      else setErrorMessage('Could not update this age rating. Try again.');
    }
  };

  const reload = async () => {
    try {
      await cache.fetchQuery({
        ...titleDetailQuery(titleId),
        staleTime: 0,
      });
      discard();
    } catch {
      setErrorMessage('Could not reload the latest rating. Try again.');
    }
  };

  const title = rating ? `${boardLabel(rating.board)} rating` : 'Add age rating';
  const options = board
    ? categories.map((category) => ({
        value: category.code,
        label: `${formatContentRating(board, category.code)} · ${describeDesignation(category.designation, category.minimumAge)}`,
      }))
    : [];

  return (
    <InspectorPanel
      id={id}
      title={title}
      onClose={onClose}
      footer={
        canManage &&
        (canEdit || manual) && (
          <EditorFooter
            standalone={false}
            dirty={dirty}
            saving={saving}
            disabled={!validCode || conflict}
            onSave={() => void save(values.code, draft?.revision ?? revision)}
            onDiscard={discard}
            onDelete={
              manual && !conflict ? () => setClearRevision(draft?.revision ?? revision) : undefined
            }
            deleteLabel="Clear override"
          />
        )
      }
    >
      <Stack gap="md">
        {rating ? (
          <Group
            justify="space-between"
            align="center"
            gap="xs"
          >
            <Text
              size="sm"
              c="dimmed"
            >
              {boardRegion(rating.board)}
            </Text>
            <SourceMarker
              label="Age rating"
              value={dirty ? 'Manual' : ratingSourceLabel(rating.sourceId)}
              disabled={saving}
            >
              {canEdit && !manual && validCode
                ? (close) => (
                    <Button
                      variant="subtle"
                      size="compact-sm"
                      onClick={() => {
                        if (dirty) discard();
                        else
                          change({
                            forceManual: true,
                          });
                        close();
                      }}
                    >
                      {dirty
                        ? `Use ${ratingSourceLabel(rating.sourceId)} rating`
                        : 'Use as manual rating'}
                    </Button>
                  )
                : undefined}
            </SourceMarker>
          </Group>
        ) : (
          <>
            <NativeSelect
              label="Rating board"
              required
              value={board}
              disabled={catalogLoading || catalogError || saving || boards.length === 0}
              data={[
                {
                  value: '',
                  label: 'Choose a board',
                  disabled: true,
                },
                ...boards.map(({ board: key }) => ({
                  value: key,
                  label: `${boardLabel(key)} · ${boardRegion(key)}`,
                })),
              ]}
              onChange={(event) => selectBoard(event.currentTarget.value)}
            />
            {!catalogLoading && !catalogError && boards.length === 0 && (
              <Text
                size="sm"
                c="dimmed"
              >
                Every available board has a rating.
              </Text>
            )}
          </>
        )}
        {canEdit && (
          <>
            <NativeSelect
              label="Age rating"
              required
              value={values.code}
              disabled={saving}
              onChange={(event) =>
                change({
                  code: event.currentTarget.value,
                })
              }
              data={[
                {
                  value: '',
                  label: 'Choose a rating',
                  disabled: true,
                },
                ...(!validCode && values.code && board
                  ? [
                      {
                        value: values.code,
                        label: formatContentRating(board, values.code),
                        disabled: true,
                      },
                    ]
                  : []),
                ...options,
              ]}
            />
            {board && values.code && (
              <Group
                justify="space-between"
                align="center"
              >
                <RatingMark
                  {...ratingPresentation(reference, {
                    board,
                    code: values.code,
                  })}
                  size={38}
                />
                {!rating && (
                  <SourceMarker
                    label="Age rating"
                    value="Manual"
                  />
                )}
              </Group>
            )}
            <TagsInput
              label="Descriptors"
              description="Press Enter to add a descriptor."
              value={values.descriptors}
              searchValue={values.descriptorInput}
              onChange={(descriptors) =>
                change({
                  descriptors,
                })
              }
              onSearchChange={(descriptorInput) =>
                change({
                  descriptorInput,
                })
              }
              splitChars={[]}
              disabled={saving}
            />
            <Textarea
              label="Synopsis"
              value={values.synopsis}
              onChange={(event) =>
                change({
                  synopsis: event.currentTarget.value,
                })
              }
              disabled={saving}
              autosize
              minRows={3}
              maxRows={10}
            />
          </>
        )}
        {!canEdit && rating && (
          <>
            <Group
              gap="sm"
              align="center"
            >
              <RatingMark
                {...ratingPresentation(reference, rating)}
                size={38}
              />
              <Text size="sm">{describeDesignation(rating.designation, rating.minimumAge)}</Text>
            </Group>
            {rating.descriptors?.length ? (
              <div>
                <Text
                  className="romd-card-title"
                  size="sm"
                >
                  Descriptors
                </Text>
                <Text size="sm">{rating.descriptors.join(', ')}</Text>
              </div>
            ) : null}
            {rating.synopsis && (
              <div>
                <Text
                  className="romd-card-title"
                  size="sm"
                >
                  Synopsis
                </Text>
                <Text size="sm">{rating.synopsis}</Text>
              </div>
            )}
          </>
        )}
        {conflict && (
          <TitleConflictAlert
            draftLabel="age rating"
            onReload={reload}
          />
        )}
        {errorMessage && (
          <Alert
            color="red"
            role="alert"
          >
            {errorMessage}
          </Alert>
        )}
      </Stack>
      <Modal
        opened={clearRevision !== undefined}
        onClose={() => setClearRevision(undefined)}
        title={board ? `Clear the ${boardLabel(board)} override?` : 'Clear rating override?'}
        centered
      >
        <Stack>
          <Text size="sm">ROMD will use the provider rating again when one is available.</Text>
          <Group justify="flex-end">
            <Button
              variant="default"
              onClick={() => setClearRevision(undefined)}
            >
              Cancel
            </Button>
            <Button
              color="red"
              onClick={() => {
                if (!clearRevision) return;
                setClearRevision(undefined);
                void save(null, clearRevision);
              }}
            >
              Clear override
            </Button>
          </Group>
        </Stack>
      </Modal>
    </InspectorPanel>
  );
}
