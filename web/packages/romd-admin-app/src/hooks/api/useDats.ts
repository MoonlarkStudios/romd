import { notifications } from '@mantine/notifications';
import type { Dat, SourceLifecycleStatus, SourceStatus, UnroutedDat } from '@romd/admin-api-client';
import {
  assignDatPlatform,
  listDats,
  listDatsByPlatform,
  listUnroutedDats,
  setDatSourceStatus,
} from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { authenticatedDownload } from '../../utils/download';
import { romKeys } from './useRoms';
import { titleDetailKeys } from './useTitleDetail';

export const datKeys = {
  all: [
    'dats',
  ] as const,
  lists: () =>
    [
      ...datKeys.all,
      'list',
    ] as const,
  list: (filters?: { systemKey?: string }) =>
    [
      ...datKeys.lists(),
      filters,
    ] as const,
  byPlatform: (systemKey: string) =>
    [
      ...datKeys.lists(),
      {
        systemKey,
      },
    ] as const,
  unrouted: () =>
    [
      ...datKeys.all,
      'unrouted',
    ] as const,
  details: () =>
    [
      ...datKeys.all,
      'detail',
    ] as const,
  detail: (id: string) =>
    [
      ...datKeys.details(),
      id,
    ] as const,
};

export function useDats() {
  return useQuery({
    queryKey: datKeys.list(),
    queryFn: async () => {
      const response = await listDats();
      if (response.error) {
        throw new Error('Failed to fetch DATs');
      }
      return response.data as Dat[];
    },
  });
}

export function useDatsByPlatform(systemKey: string | undefined) {
  return useQuery({
    queryKey: datKeys.byPlatform(systemKey ?? ''),
    queryFn: async ({ signal }) => {
      if (!systemKey) throw new Error('Platform ID is required');
      const response = await listDatsByPlatform({
        path: {
          systemKey,
        },
        signal,
      });
      if (response.error) {
        throw new Error('Failed to fetch DATs');
      }
      return response.data as Dat[];
    },
    enabled: !!systemKey,
  });
}

export function useUnroutedDats() {
  return useQuery({
    queryKey: datKeys.unrouted(),
    queryFn: async () => {
      const response = await listUnroutedDats();
      if (response.error) {
        throw new Error('Failed to fetch unrouted DATs');
      }
      return response.data as UnroutedDat[];
    },
  });
}

export function downloadDatSource(dat: Pick<Dat, 'id' | 'name'>): Promise<void> {
  const fallbackName = dat.name.trim().replace(/[\\/:*?"<>|]/g, '-') || 'source-dat';
  return authenticatedDownload(`/api/dats/${dat.id}/download`, `${fallbackName}.dat`);
}

interface AssignPlatformParams {
  datId: string;
  systemKey: string;
}

export function useAssignDatPlatform() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ datId, systemKey }: AssignPlatformParams) => {
      const response = await assignDatPlatform({
        path: {
          datId,
        },
        body: {
          systemKey,
        },
      });

      if (response.error) {
        throw new Error('Failed to assign platform');
      }

      return response.data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({
        queryKey: datKeys.all,
      });
      // Routing a DAT recatalogs its matched ROMs — stats and lists are stale
      queryClient.invalidateQueries({
        queryKey: romKeys.all,
      });
    },
  });
}

interface SetSourceStatusParams {
  datId: string;
  /** Names the source in the success / failure toast */
  datName: string;
  status: SourceLifecycleStatus;
}

export function useSetDatSourceStatus() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ datId, status }: SetSourceStatusParams) => {
      const response = await setDatSourceStatus({
        path: {
          datId,
        },
        body: {
          status,
        },
      });

      if (response.error) {
        throw new Error('Failed to update source status');
      }

      return response.data as SourceStatus;
    },
    onSuccess: (_data, { datName, status }) => {
      notifications.show({
        title: 'Source status updated',
        message: `${datName} is now ${status}.`,
        color: 'green',
      });
      queryClient.invalidateQueries({
        queryKey: datKeys.all,
      });
      queryClient.invalidateQueries({
        queryKey: [
          'managed-systems',
        ],
      });
      // Source status gates title backing visibility — title detail reads are stale
      queryClient.invalidateQueries({
        queryKey: titleDetailKeys.all,
      });
    },
    onError: (_error, { datName, status }) => {
      notifications.show({
        title: 'Status change failed',
        message: `Could not set ${datName} to ${status}.`,
        color: 'red',
      });
    },
  });
}
