import {
  type ProviderGameDto,
  type ProviderGameSearchResultDto,
  resolveMetadataProviderGame,
  searchMetadataProvider,
} from '@romd/admin-api-client';
import { useCallback, useEffect, useRef, useState } from 'react';

type LookupMode = 'search' | 'resolve';
interface LookupData {
  items: ProviderGameDto[];
  search?: ProviderGameSearchResultDto;
}
interface LookupState {
  data: LookupData | null;
  error: Error | null;
  isPending: boolean;
}

/** An explicit lookup belongs to one picker draft; edits invalidate every earlier response. */
export function useProviderGameLookup(providerId: string) {
  const [state, setState] = useState<LookupState>({
    data: null,
    error: null,
    isPending: false,
  });
  const pending = useRef<AbortController | null>(null);
  const generation = useRef(0);
  const cancel = useCallback(() => {
    generation.current += 1;
    pending.current?.abort();
    pending.current = null;
  }, []);
  useEffect(
    () => cancel,
    [
      cancel,
    ],
  );

  const reset = () => {
    cancel();
    setState({
      data: null,
      error: null,
      isPending: false,
    });
  };
  const lookup = async (input: string, mode: LookupMode) => {
    cancel();
    const owner = generation.current;
    const controller = new AbortController();
    pending.current = controller;
    setState({
      data: null,
      error: null,
      isPending: true,
    });
    try {
      let data: LookupData;
      if (mode === 'resolve') {
        const result = await resolveMetadataProviderGame({
          path: {
            providerId,
          },
          query: {
            input,
          },
          signal: controller.signal,
        });
        if (result.error || !result.data)
          throw new Error('Could not find that provider game. Check the ID or URL, or try again.');
        data = {
          items: [
            result.data,
          ],
        };
      } else {
        const result = await searchMetadataProvider({
          path: {
            providerId,
          },
          query: {
            query: input,
          },
          signal: controller.signal,
        });
        if (result.error || !result.data)
          throw new Error('Could not find provider games. Check the input or try again.');
        if (
          !Array.isArray(result.data.items) ||
          !Number.isInteger(result.data.limit) ||
          result.data.limit < 1 ||
          result.data.items.length > result.data.limit ||
          ![
            'Complete',
            'ProviderLimited',
            'BudgetLimited',
          ].includes(result.data.completeness)
        )
          throw new Error('The provider returned an invalid search result. Try again.');
        data = {
          items: result.data.items,
          search: result.data,
        };
      }
      if (generation.current === owner && !controller.signal.aborted)
        setState({
          data,
          error: null,
          isPending: false,
        });
    } catch (error) {
      if (generation.current === owner && !controller.signal.aborted)
        setState({
          data: null,
          error: error instanceof Error ? error : new Error('Provider games could not be loaded.'),
          isPending: false,
        });
    } finally {
      if (generation.current === owner) pending.current = null;
    }
  };
  return {
    ...state,
    lookup,
    reset,
  };
}
