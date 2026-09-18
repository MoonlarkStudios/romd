import type { ConsumerReleaseManifestDto } from '@romd/consumer-api-client';
import { useCallback, useEffect, useReducer, useRef } from 'react';
import {
  type DownloadFailure,
  type DownloadVerificationStatus,
  revokeVerifiedDownload,
  type VerifiedDownload,
  verifyManifestItemDownload,
} from '../services/downloadVerification';

interface DownloadItemState {
  status: DownloadVerificationStatus;
  file?: VerifiedDownload;
  failure?: DownloadFailure;
}

type DownloadState = Record<string, DownloadItemState>;

type DownloadAction =
  | {
      type: 'reset';
      state: DownloadState;
    }
  | {
      type: 'downloading';
      relativePath: string;
    }
  | {
      type: 'verified';
      relativePath: string;
      file: VerifiedDownload;
    }
  | {
      type: 'failed';
      relativePath: string;
      failure: DownloadFailure;
    }
  | {
      type: 'unavailable';
      relativePath: string;
      failure: DownloadFailure;
    };

export function useReleaseDownloads(manifest: ConsumerReleaseManifestDto | null | undefined) {
  const [state, dispatch] = useReducer(downloadReducer, {});
  const stateRef = useRef(state);

  useEffect(() => {
    stateRef.current = state;
  }, [state]);

  useEffect(() => {
    const nextState = Object.fromEntries(
      (manifest?.items ?? []).map((item) => [
        item.relativePath,
        {
          status: item.isAvailable ? 'idle' : 'unavailable',
          failure: item.isAvailable
            ? undefined
            : {
                message: 'This file is listed in the manifest but is not available.',
                retryRequiresManifest: false,
              },
        } satisfies DownloadItemState,
      ]),
    );

    dispatch({
      type: 'reset',
      state: nextState,
    });
  }, [manifest]);

  useEffect(() => () => revokeDownloads(stateRef.current), []);

  const downloadItem = useCallback(
    async (relativePath: string) => {
      const item = manifest?.items.find((candidate) => candidate.relativePath === relativePath);
      if (!item) {
        return null;
      }

      dispatch({
        type: 'downloading',
        relativePath,
      });

      const result = await verifyManifestItemDownload(item);

      if (result.status === 'verified') {
        dispatch({
          type: 'verified',
          relativePath,
          file: result.file,
        });
        return result;
      }

      dispatch({
        type: result.status,
        relativePath,
        failure: result.failure,
      });
      return result;
    },
    [manifest],
  );

  return {
    downloads: state,
    downloadItem,
  };
}

function downloadReducer(state: DownloadState, action: DownloadAction): DownloadState {
  switch (action.type) {
    case 'reset':
      revokeDownloads(state);
      return action.state;
    case 'downloading':
      return updateItem(state, action.relativePath, {
        status: 'downloading',
      });
    case 'verified':
      return updateItem(state, action.relativePath, {
        status: 'verified',
        file: action.file,
      });
    case 'failed':
    case 'unavailable':
      return updateItem(state, action.relativePath, {
        status: action.type,
        failure: action.failure,
      });
  }
}

function updateItem(state: DownloadState, relativePath: string, next: DownloadItemState): DownloadState {
  const current = state[relativePath];
  if (current?.file && current.file.objectUrl !== next.file?.objectUrl) {
    revokeVerifiedDownload(current.file);
  }

  return {
    ...state,
    [relativePath]: next,
  };
}

function revokeDownloads(state: DownloadState): void {
  for (const item of Object.values(state)) {
    if (item.file) {
      revokeVerifiedDownload(item.file);
    }
  }
}
