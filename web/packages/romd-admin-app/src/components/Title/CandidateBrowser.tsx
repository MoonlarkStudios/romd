import {
  Alert,
  Button,
  Group,
  Loader,
  Select,
  SimpleGrid,
  Stack,
  Switch,
  Text,
  UnstyledButton,
} from '@mantine/core';
import type { ArtworkCandidate, ArtworkProvider } from '@romd/admin-api-client';
import { listTitleArtworkCandidates, replaceHiddenArtworkCandidate } from '@romd/admin-api-client';
import { useInfiniteQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { invalidateTitle } from '../../hooks/api/titleCache';
import { ifMatch } from '../../hooks/api/titleConflict';
import { artworkKeys, storeArtwork, useArtworkSelections } from '../../hooks/api/useArtwork';
import type { TitleDetailView as TitleDetail } from '../../hooks/api/useTitleDetail';
import { backdropSuitable, type ArtworkRole as Role } from '../artworkPresentation';
import classes from './ArtworkWorkspace.module.css';
import { CandidateImage } from './CandidateImage';

/** Browses one provider's candidates for a role; choosing one previews it, and hiding one excludes it for good. */
export function CandidateBrowser({
  titleId,
  title,
  role,
  provider,
  gameId,
  selectedReference,
  disabled,
  allowHide,
  onChoose,
}: {
  titleId: string;
  title: TitleDetail;
  role: Role;
  provider: ArtworkProvider;
  gameId: string;
  selectedReference?: string;
  disabled: boolean;
  allowHide: boolean;
  onChoose: (candidate: ArtworkCandidate) => Promise<void>;
}) {
  const cache = useQueryClient();
  const family = useArtworkSelections(titleId).data;
  const [dimension, setDimension] = useState<string | null>(null);
  const [style, setStyle] = useState<string | null>(null);

  const [visible, setVisible] = useState(12);
  const [suitableOnly, setSuitableOnly] = useState(
    role === 'Backdrop' && provider.id !== 'steamgriddb',
  );
  const [pending, setPending] = useState(false);
  const [hiding, setHiding] = useState(false);
  const unavailable = pending || disabled;
  const [error, setError] = useState<string | null>(null);
  const capability = provider.roles.find((item) => item.role === role);
  const candidates = useInfiniteQuery({
    queryKey: [
      ...artworkKeys.candidates(titleId),
      provider.id,
      gameId,
      role,
      dimension,
      style,
    ],
    initialPageParam: null as string | null,
    queryFn: async ({ pageParam, signal }) => {
      const response = await listTitleArtworkCandidates({
        path: {
          titleId,
        },
        query: {
          providerId: provider.id,
          gameId,
          role,
          dimension: dimension ?? undefined,
          style: style ?? undefined,
          cursor: pageParam ?? undefined,
        },
        signal,
      });
      if (response.error || !response.data)
        throw new Error(response.error?.detail ?? 'Could not browse artwork.');
      if (
        !Object.hasOwn(response.data, 'nextCursor') ||
        response.data.hasNextPage !== (typeof response.data.nextCursor === 'string') ||
        (response.data.hasNextPage && !response.data.nextCursor?.trim()) ||
        (pageParam !== null && response.data.nextCursor === pageParam)
      )
        throw new Error(
          'The provider returned an invalid continuation. Refresh candidates and try again.',
        );
      return response.data;
    },
    getNextPageParam: (page) => (page.hasNextPage ? page.nextCursor : undefined),
    staleTime: 0,
    refetchOnWindowFocus: false,
  });
  const items = Array.from(
    new Map(
      (candidates.data?.pages.flatMap((page) => page.items) ?? []).map((item) => [
        item.candidateId,
        item,
      ]),
    ).values(),
  );
  const displayed =
    role === 'Backdrop' && suitableOnly
      ? items
          .filter((item) => backdropSuitable(item.width, item.height))
          .sort(
            (a, b) =>
              Math.abs(Math.log(a.width / a.height / (16 / 9))) -
                Math.abs(Math.log(b.width / b.height / (16 / 9))) ||
              b.width * b.height - a.width * a.height,
          )
      : items;
  const changeFilters = (nextDimension: string | null, nextStyle: string | null) => {
    setDimension(nextDimension);
    setStyle(nextStyle);
    setVisible(12);
    setError(null);
  };
  const apply = async (candidate: ArtworkCandidate) => {
    setPending(true);
    setError(null);
    try {
      await onChoose(candidate);
    } catch {
      setError('Preview expired or unavailable. Refresh candidates and try again.');
    } finally {
      setPending(false);
    }
  };
  return (
    <Stack>
      {!!(capability?.dimensions.length || capability?.styles.length) && (
        <Group grow>
          {!!capability?.dimensions.length && (
            <Select
              label="Dimensions"
              placeholder="All supported dimensions"
              clearable
              value={dimension}
              data={capability.dimensions}
              onChange={(value) => changeFilters(value, style)}
              disabled={unavailable}
            />
          )}
          {!!capability?.styles.length && (
            <Select
              label="Style"
              placeholder="All styles"
              clearable
              value={style}
              data={capability.styles}
              onChange={(value) => changeFilters(dimension, value)}
              disabled={unavailable}
            />
          )}
        </Group>
      )}
      {role === 'Backdrop' && (
        <Stack gap="xs">
          <Text size="sm">
            Choose a scene without a built-in game logo. Thumbnails show a centered 16:9 crop.
            Select an image to adjust its focal point and check the composition preview.
          </Text>
          <Switch
            label="Suitable landscape dimensions"
            description="At least 1200 × 720; includes crops from 1.3:1 to 2.1:1. Turn off to inspect all artwork."
            checked={suitableOnly}
            onChange={(event) => {
              setSuitableOnly(event.currentTarget.checked);
              setVisible(12);
            }}
          />
        </Stack>
      )}
      {candidates.isLoading && <Loader aria-label="Loading candidates" />}
      {candidates.isError && <Alert color="red">{candidates.error.message}</Alert>}
      <Group justify="space-between">
        <Text
          size="sm"
          c="dimmed"
        >
          {displayed.length} images
        </Text>
        <Button
          variant="subtle"
          disabled={unavailable}
          onClick={() => {
            void candidates.refetch();
          }}
        >
          Refresh candidates
        </Button>
      </Group>
      <SimpleGrid
        type="container"
        cols={{
          base: 2,
          '36em': 3,
          '56em': 4,
        }}
      >
        {displayed.slice(0, visible).map((candidate) => (
          <Stack
            key={candidate.reference}
            gap={4}
          >
            <UnstyledButton
              aria-label={`Select artwork ${candidate.providerAssetId}`}
              aria-pressed={selectedReference === candidate.reference}
              disabled={unavailable}
              onClick={() => void apply(candidate)}
              className={classes.candidate}
            >
              <CandidateImage
                titleId={titleId}
                candidate={candidate}
                title={title.name}
              />
              <Text
                size="xs"
                mt="xs"
              >
                {candidate.width} × {candidate.height}
                {candidate.style ? ` · ${candidate.style}` : ''}
                {role === 'Backdrop' && candidate.width / candidate.height > 2.1
                  ? ' · Wide image · Requires cropping'
                  : ''}
              </Text>
            </UnstyledButton>
            <Button
              variant="subtle"
              color="gray"
              size="compact-xs"
              aria-label={`Hide image ${candidate.providerAssetId}`}
              disabled={unavailable || !allowHide}
              onClick={async () => {
                setPending(true);
                setHiding(true);
                setError(null);
                try {
                  if (!family) return;
                  // Hiding is a write to the artwork family, under the family revision this browser showed.
                  const response = await replaceHiddenArtworkCandidate({
                    path: {
                      titleId,
                      candidateId: candidate.candidateId,
                    },
                    headers: ifMatch(family.revision),
                    body: {
                      candidateReference: candidate.reference,
                    },
                  });
                  // A hide rotates the title's revision, so every surface of the title reloads, as after a delete.
                  if (response.response?.status === 412) {
                    setError('The artwork changed. It was reloaded; hide the image again.');
                    await invalidateTitle(cache, titleId);
                    return;
                  }
                  if (response.error || !response.data) {
                    setError(response.error?.detail ?? 'Could not hide image.');
                    return;
                  }
                  await storeArtwork(cache, titleId, response.data);
                  await candidates.refetch();
                  await invalidateTitle(cache, titleId);
                } catch {
                  setError('Could not hide image. Try again.');
                } finally {
                  setPending(false);
                  setHiding(false);
                }
              }}
            >
              Hide image
            </Button>
          </Stack>
        ))}
      </SimpleGrid>
      {!candidates.isLoading && !candidates.isError && displayed.length === 0 && (
        <Text>
          {candidates.hasNextPage
            ? 'No candidates on this page. More candidates may be available.'
            : 'No artwork matches these filters.'}
        </Text>
      )}
      {(visible < displayed.length || candidates.hasNextPage) && (
        <Button
          variant="light"
          loading={candidates.isFetchingNextPage}
          disabled={unavailable || candidates.isFetching}
          onClick={() => {
            if (visible >= displayed.length && !candidates.isFetching)
              void candidates.fetchNextPage({
                cancelRefetch: false,
              });
            setVisible((count) => count + 12);
          }}
        >
          More candidates
        </Button>
      )}
      {pending && (
        <Text
          size="sm"
          role="status"
        >
          {hiding ? 'Hiding image…' : 'Loading preview…'}
        </Text>
      )}
      {error && <Alert color="red">{error}</Alert>}
    </Stack>
  );
}
