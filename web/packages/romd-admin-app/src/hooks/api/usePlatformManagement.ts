import type { PlatformAlias } from '@romd/admin-api-client';
import { addPlatformAlias, createSystems } from '@romd/admin-api-client';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { platformKeys } from './usePlatforms';

interface CreatePlatformParams {
  name: string;
  shortName: string;
  manufacturerKey?: string;
}

export function useCreatePlatform() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ name, shortName, manufacturerKey }: CreatePlatformParams) => {
      const response = await createSystems({
        body: {
          name,
          key: shortName,
          compactLabel: name.length <= 40 ? name : shortName.slice(0, 40),
          manufacturerKeys: manufacturerKey ? [manufacturerKey] : [],
        },
      });

      if (response.error || !response.data) {
        throw new Error(
          response.response.status === 409
            ? `A system with key "${shortName}" already exists`
            : 'Failed to create system',
        );
      }

      return response.data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: platformKeys.all });
      queryClient.invalidateQueries({ queryKey: ['reference-catalog'] });
    },
  });
}

interface AddPlatformAliasParams {
  systemKey: string;
  type: 'name' | 'provider';
  value: string;
  provider?: string;
}

export function useAddPlatformAlias() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ systemKey, type, value, provider }: AddPlatformAliasParams) => {
      const response = await addPlatformAlias({
        path: { systemKey },
        body: { type, value, provider },
      });

      if (response.error || !response.data) {
        throw new Error(
          response.response.status === 409
            ? `Alias "${value}" is already in use`
            : 'Failed to add alias',
        );
      }

      return response.data as PlatformAlias;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: platformKeys.all });
    },
  });
}
