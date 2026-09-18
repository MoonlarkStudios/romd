import { useQuery } from '@tanstack/react-query';
import { hasBrowserPlaybackPlatform } from '../services/browserPlayback';
import { getDownloadVerificationUnavailableReason } from '../services/downloadVerification';
import { fetchPlayerCapability } from '../services/playerBridge';

export const browserPlaybackCapabilityQueryKey = [
  'consumer',
  'browser-playback-capability',
] as const;

export function useBrowserPlaybackCapability() {
  return useQuery({
    queryKey: browserPlaybackCapabilityQueryKey,
    queryFn: ({ signal }) => fetchPlayerCapability(signal),
    staleTime: 60_000,
  });
}

export function useBrowserPlaybackAvailability(systemKey: string) {
  const query = useBrowserPlaybackCapability();
  const verificationReason = getDownloadVerificationUnavailableReason();
  const capability = query.data;

  if (verificationReason) {
    return { available: false, reason: verificationReason, loading: false } as const;
  }
  if (query.isLoading || !capability) {
    return { available: false, reason: 'Checking browser player availability.', loading: true } as const;
  }
  if (capability.status === 'unavailable') {
    return { available: false, reason: capability.reason, loading: false } as const;
  }
  if (!hasBrowserPlaybackPlatform(systemKey, capability.capability.cores)) {
    return {
      available: false,
      reason: 'This platform is not mapped to an available browser emulator core.',
      loading: false,
    } as const;
  }

  return { available: true, reason: null, loading: false } as const;
}
