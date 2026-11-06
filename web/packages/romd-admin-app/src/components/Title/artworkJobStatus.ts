interface ArtworkJobState {
  phase: string;
  isTerminal?: boolean;
  hasErrors?: boolean;
  wasSuperseded?: boolean;
}

export function artworkJobStatus(job: ArtworkJobState) {
  if (job.phase === 'Completed') {
    return job.wasSuperseded
      ? {
          color: 'gray',
          title: 'Artwork request superseded',
          message: 'A newer artwork choice took precedence. This request did not replace it.',
        }
      : {
          color: 'teal',
          title: 'Artwork import completed',
          message: 'The selected artwork was imported. Artwork selections have been refreshed.',
        };
  }
  if (job.phase === 'Failed') {
    return {
      color: 'red',
      title: 'Artwork import failed',
      message: 'The import failed. Your previous selection was preserved.',
    };
  }
  if (job.phase === 'Cancelled') {
    return {
      color: 'gray',
      title: 'Artwork import cancelled',
      message: 'The import was cancelled. Existing artwork remains available.',
    };
  }
  return job.isTerminal
    ? {
        color: 'blue',
        title: 'Artwork import finished',
        message: 'Artwork selections have been refreshed.',
      }
    : {
        color: 'blue',
        title: 'Artwork import in progress',
        message: 'Your previous selection stays visible until the new files are ready.',
      };
}
