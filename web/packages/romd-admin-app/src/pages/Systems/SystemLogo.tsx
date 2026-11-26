import type { ReferenceAsset } from '@romd/admin-api-client';
import { useReferenceCatalog } from '@romd/consumer-ui';
import { IconDeviceGamepad2 } from '@tabler/icons-react';
import { useMemo, useState } from 'react';
import classes from './SystemLogo.module.css';

export function useSystemIcons(): ReadonlyMap<string, ReferenceAsset> {
  const systems = useReferenceCatalog()?.systems;
  return useMemo(() => {
    const icons = new Map<string, ReferenceAsset>();
    for (const system of systems ?? []) {
      if (system.icon) icons.set(system.key, system.icon);
    }
    return icons;
  }, [
    systems,
  ]);
}

/** A decorative mark shown beside the visible system name. */
export function SystemLogo({
  icon,
  size = 'md',
}: {
  icon?: ReferenceAsset | null;
  size?: 'sm' | 'md' | 'lg' | 'preview';
}) {
  const [failedUrl, setFailedUrl] = useState<string>();
  return (
    <span
      aria-hidden="true"
      className={classes.logo}
      data-size={size}
    >
      {icon && icon.url !== failedUrl ? (
        <img
          alt=""
          src={icon.url}
          loading="lazy"
          decoding="async"
          className={icon.monochrome ? classes.monochrome : undefined}
          onError={() => setFailedUrl(icon.url)}
        />
      ) : (
        <IconDeviceGamepad2 size="50%" />
      )}
    </span>
  );
}
