import type { ReferenceAsset } from '@romd/consumer-api-client';
import { IconDeviceGamepad2 } from '@tabler/icons-react';
import { useState } from 'react';
import classes from './Consoles.module.css';

export function useAvailableArtwork(asset: ReferenceAsset | null | undefined) {
  const [failedUrl, setFailedUrl] = useState<string>();
  const url = asset?.url;

  return {
    src: url && url !== failedUrl ? url : undefined,
    onError: () => setFailedUrl(url),
  };
}

export function ConsoleLogo({
  icon,
  fallback = false,
}: {
  icon?: ReferenceAsset | null;
  fallback?: boolean;
}) {
  const image = useAvailableArtwork(icon);
  if (!image.src)
    return fallback ? (
      <IconDeviceGamepad2
        size={48}
        aria-hidden
      />
    ) : null;
  return (
    <img
      {...image}
      alt=""
      className={classes.logo}
      data-monochrome={icon?.monochrome || undefined}
      decoding="async"
    />
  );
}
