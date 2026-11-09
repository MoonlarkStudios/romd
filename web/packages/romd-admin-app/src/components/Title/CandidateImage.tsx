import { Box, Image, Loader, Text } from '@mantine/core';
import { type ArtworkCandidate, previewArtworkCandidate } from '@romd/admin-api-client';
import { useEffect, useState } from 'react';
import { ArtworkFrame } from '../ArtworkFrame';
import { ArtworkSurface } from '../ArtworkSurface';

export function CandidateImage({
  titleId,
  candidate,
  title,
  gallery = false,
  expanded = false,
}: {
  titleId: string;
  candidate: ArtworkCandidate;
  title: string;
  gallery?: boolean;
  expanded?: boolean;
}) {
  const [image, setImage] = useState<{
    reference: string;
    url: string;
  } | null>(null);
  const [error, setError] = useState(false);
  useEffect(() => {
    const abort = new AbortController();
    let url: string | undefined;
    setError(false);
    void previewArtworkCandidate({
      path: {
        titleId,
      },
      body: {
        candidateReference: candidate.reference,
      },
      signal: abort.signal,
      parseAs: 'blob',
    })
      .then((response) => {
        if (abort.signal.aborted) return;
        const data: unknown = response.data;
        if (response.error || !(data instanceof Blob)) {
          setError(true);
          return;
        }
        url = URL.createObjectURL(data);
        setImage({
          reference: candidate.reference,
          url,
        });
      })
      .catch(() => {
        if (!abort.signal.aborted) setError(true);
      });
    return () => {
      abort.abort();
      if (url) URL.revokeObjectURL(url);
    };
  }, [
    titleId,
    candidate.reference,
  ]);
  if (error || !image || image.reference !== candidate.reference)
    return (
      <Box
        style={{
          aspectRatio:
            candidate.role === 'Backdrop'
              ? '16 / 9'
              : candidate.role === 'Hero'
                ? '1920 / 620'
                : candidate.role === 'Logo'
                  ? '2 / 1'
                  : '2 / 3',
          display: 'grid',
          placeItems: 'center',
        }}
      >
        {error ? (
          <Text
            c="red"
            size="xs"
          >
            Preview unavailable
          </Text>
        ) : (
          <Loader
            size="sm"
            aria-label="Loading artwork preview"
          />
        )}
      </Box>
    );
  if (gallery || expanded)
    return (
      <ArtworkSurface background={candidate.role === 'Logo' ? 'checkerboard' : 'dark'}>
        <Image
          src={image.url}
          alt={title}
          h={expanded ? undefined : 150}
          mah={expanded ? '65vh' : undefined}
          fit="contain"
        />
      </ArtworkSurface>
    );
  return (
    <ArtworkFrame
      artworkRole={
        candidate.role === 'Backdrop'
          ? 'Backdrop'
          : candidate.role === 'Hero'
            ? 'Hero'
            : candidate.role === 'Logo'
              ? 'Logo'
              : 'Poster'
      }
      src={image.url}
      title={title}
      fit={candidate.role === 'Backdrop' ? 'Cover' : 'Contain'}
    />
  );
}
