import { IconDeviceGamepad2 } from '@tabler/icons-react';
import { useState } from 'react';
import classes from './GameCover.module.css';

export function GameCover({
  url,
  className = classes.thumbnail,
  iconSize = 20,
}: {
  url?: string | null;
  className?: string;
  iconSize?: number;
}) {
  const [failedUrl, setFailedUrl] = useState<string>();
  return url && url !== failedUrl ? (
    <img
      className={`${classes.cover} ${className}`}
      src={url}
      alt=""
      loading="lazy"
      onError={() => setFailedUrl(url)}
    />
  ) : (
    <span
      className={`${classes.cover} ${className}`}
      aria-hidden="true"
    >
      <IconDeviceGamepad2 size={iconSize} />
    </span>
  );
}
