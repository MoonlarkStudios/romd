import { IconDeviceGamepad2 } from '@tabler/icons-react';
import { useState } from 'react';

export function CollectionGameCover({
  url,
  className,
  iconSize = 20,
}: {
  url?: string | null;
  className: string;
  iconSize?: number;
}) {
  const [failedUrl, setFailedUrl] = useState<string>();
  return url && url !== failedUrl ? (
    <img
      className={className}
      src={url}
      alt=""
      loading="lazy"
      onError={() => setFailedUrl(url)}
    />
  ) : (
    <span
      className={className}
      aria-hidden="true"
    >
      <IconDeviceGamepad2 size={iconSize} />
    </span>
  );
}
