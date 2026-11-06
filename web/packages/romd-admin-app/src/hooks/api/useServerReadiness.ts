import { client } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';

type CheckStatus = 'healthy' | 'degraded' | 'unready';
export interface ServerReadiness {
  status: 'ready' | 'degraded' | 'unready';
  checks: {
    name: string;
    status: CheckStatus;
  }[];
}

function isReadiness(value: unknown): value is ServerReadiness {
  if (!value || typeof value !== 'object' || !('status' in value) || !('checks' in value))
    return false;
  return (
    [
      'ready',
      'degraded',
      'unready',
    ].includes(String(value.status)) &&
    Array.isArray(value.checks) &&
    value.checks.length > 0 &&
    value.checks.every(
      (check: unknown) =>
        check !== null &&
        typeof check === 'object' &&
        'name' in check &&
        typeof check.name === 'string' &&
        check.name.length > 0 &&
        'status' in check &&
        [
          'healthy',
          'degraded',
          'unready',
        ].includes(String(check.status)),
    )
  );
}

/** Readiness is an anonymous protocol surface, deliberately excluded from OpenAPI. */
export function useServerReadiness() {
  return useQuery({
    queryKey: [
      'server-readiness',
    ],
    queryFn: async ({ signal }): Promise<ServerReadiness> => {
      const origin = client.getConfig().baseUrl || window.location.origin;
      const response = await fetch(new URL('/health/ready', origin), {
        signal: AbortSignal.any([
          signal,
          AbortSignal.timeout(10_000),
        ]),
        credentials: 'omit',
        cache: 'no-store',
        redirect: 'error',
      });
      const body: unknown = await response.json();
      if (
        !isReadiness(body) ||
        (body.status === 'unready' ? response.status !== 503 : response.status !== 200) ||
        (body.status === 'ready' && body.checks.some((check) => check.status !== 'healthy'))
      )
        throw new Error('Server readiness is unavailable.');
      return body;
    },
    retry: false,
    staleTime: 60_000,
    refetchInterval: 30_000,
    refetchIntervalInBackground: false,
    refetchOnWindowFocus: true,
  });
}
