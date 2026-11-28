import type { ResolvedArtwork } from '@romd/consumer-api-client';
import { artworkAspectRatios, artworkObjectStyle } from '@romd/consumer-ui';
import {
  type CSSProperties,
  type ReactNode,
  useEffect,
  useEffectEvent,
  useLayoutEffect,
  useRef,
  useState,
} from 'react';
import { userManager } from '../../auth/userManager';
import { artworkCacheKey, loadRecentArtwork } from '../../services/artworkCache';
import { type ArtworkRole, artworkDeliveryUrl, selectArtwork } from '../../utils/artwork';

interface ArtworkImageProps {
  artwork?: ReadonlyArray<ResolvedArtwork>;
  artworkRole?: ArtworkRole;
  alt?: string;
  style?: CSSProperties;
  fallback?: ReactNode;
  missingArtworkLabel?: ReactNode;
  priority?: boolean;
  onError?: () => void;
}

export function ArtworkImage({
  artwork,
  artworkRole: role = 'Poster',
  alt = '',
  style,
  fallback,
  missingArtworkLabel,
  priority = false,
  onError,
}: ArtworkImageProps) {
  const element = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState<number | null>(null);
  const [visible, setVisible] = useState(typeof IntersectionObserver === 'undefined');
  const [account, setAccount] = useState<string | null>();
  const [cachedImage, setCachedImage] = useState<{
    key: string;
    url: string;
  } | null>(null);
  const [loaded, setLoaded] = useState<{
    requestKey: string | null;
    url: string;
  } | null>(null);
  const [failedRequest, setFailedRequest] = useState<string | null>(null);
  const reportError = useEffectEvent(() => onError?.());
  // Measure before assigning a source so the first request uses the intended size.
  const selected =
    width === null ? null : selectArtwork(artwork, role, width, window.devicePixelRatio);
  const source = artworkDeliveryUrl(selected?.url, window.location.origin);
  const requestKey = source
    ? JSON.stringify([
        source,
        selected?.contentVersion,
      ])
    : null;
  const directFailed = requestKey !== null && failedRequest === requestKey;
  const directSettled =
    directFailed || (loaded?.requestKey === requestKey && loaded?.url === source);
  const shouldCache = visible || (directFailed && priority);
  const cacheKey =
    source && account && selected?.contentVersion
      ? artworkCacheKey(window.location.origin, account, selected.contentVersion, source)
      : source;

  useEffect(() => {
    if (!source) return;
    let active = true;
    const refresh = async () => {
      try {
        const user = await userManager.getUser();
        if (active)
          setAccount(user && !user.expired ? `${user.profile.iss}:${user.profile.sub}` : null);
      } catch {
        if (active) setAccount(null);
      }
    };
    const clear = () => setAccount(null);
    void refresh();
    userManager.events.addUserLoaded(refresh);
    userManager.events.addUserUnloaded(clear);
    return () => {
      active = false;
      userManager.events.removeUserLoaded(refresh);
      userManager.events.removeUserUnloaded(clear);
    };
  }, [
    source,
  ]);

  useLayoutEffect(() => {
    const target = element.current;
    if (!target) return;
    const measure = () => setWidth(target.getBoundingClientRect().width || 200);
    measure();
    const resize = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure);
    resize?.observe(target);
    return () => resize?.disconnect();
  }, []);

  useEffect(() => {
    const target = element.current;
    if (!target) return;
    const intersection =
      typeof IntersectionObserver === 'undefined'
        ? null
        : new IntersectionObserver((entries) => {
            if (entries.some((entry) => entry.isIntersecting)) setVisible(true);
          });
    intersection?.observe(target);
    return () => intersection?.disconnect();
  }, []);

  useEffect(() => {
    if (role === 'Backdrop' || !source || !directSettled || account === undefined) return;
    if (!shouldCache) return;
    if (!cacheKey || !account) {
      if (directFailed) reportError();
      return;
    }
    const controller = new AbortController();
    let objectUrl: string | undefined;
    void loadRecentArtwork(source, cacheKey, controller.signal)
      .then((blob) => {
        // Warm the cache after the direct load. Only display a cached copy when it failed.
        if (controller.signal.aborted || !directFailed) return;
        if (!blob) {
          reportError();
          return;
        }
        objectUrl = URL.createObjectURL(blob);
        setCachedImage({
          key: cacheKey,
          url: objectUrl,
        });
      })
      .catch(() => {
        if (!controller.signal.aborted && directFailed) reportError();
      });
    return () => {
      controller.abort();
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [
    source,
    cacheKey,
    account,
    shouldCache,
    role,
    directSettled,
    directFailed,
  ]);

  const url = directFailed ? (cachedImage?.key === cacheKey ? cachedImage.url : null) : source;
  const ready = url !== null && loaded?.requestKey === requestKey && loaded?.url === url;
  const srcSet =
    role === 'Backdrop'
      ? artwork
          ?.find((item) => item.role === role)
          ?.variants.filter(
            (item) => item.name.toLowerCase() !== 'original' && item.width > 0 && item.height > 0,
          )
          .flatMap((item) => {
            const safe = artworkDeliveryUrl(item.url, window.location.origin);
            return safe
              ? [
                  `${safe} ${item.width}w`,
                ]
              : [];
          })
          .join(', ')
      : undefined;
  return (
    <div
      ref={element}
      style={{
        position: 'relative',
        width: '100%',
        aspectRatio: artworkAspectRatios[role],
        overflow: 'hidden',
        ...style,
      }}
    >
      {fallback && (
        <div
          style={{
            position: role === 'Logo' ? 'relative' : 'absolute',
            minHeight: role === 'Logo' ? 'inherit' : undefined,
            visibility: role === 'Logo' && ready ? 'hidden' : undefined,
            inset: 0,
            display: 'grid',
            placeItems:
              role === 'Logo' ? 'var(--romd-logo-fallback-alignment, center start)' : 'center',
          }}
        >
          {fallback}
        </div>
      )}
      {url && (
        <img
          className={role === 'Logo' ? undefined : 'romd-artwork-image'}
          src={url}
          alt={alt}
          srcSet={srcSet || undefined}
          sizes={srcSet ? '100vw' : undefined}
          loading={priority ? 'eager' : 'lazy'}
          fetchPriority={priority ? 'high' : undefined}
          decoding="async"
          onLoad={() =>
            setLoaded({
              requestKey,
              url,
            })
          }
          onError={() => {
            if (directFailed) setCachedImage(null);
            else setFailedRequest(requestKey);
            if (directFailed || role === 'Backdrop') onError?.();
          }}
          style={{
            position: 'absolute',
            inset: 0,
            width: '100%',
            height: '100%',
            opacity: ready ? 1 : 0,
            visibility: role === 'Logo' && !ready ? 'hidden' : undefined,
            ...artworkObjectStyle(role, selected),
          }}
        />
      )}
      {(!source || (directFailed && !ready)) && missingArtworkLabel}
    </div>
  );
}
