import type { ResolvedArtworkDto } from '@romd/consumer-api-client';
import { type CSSProperties, type ReactNode, useEffect, useRef, useState } from 'react';
import { userManager } from '../../auth/userManager';
import { artworkCacheKey, loadRecentArtwork } from '../../services/artworkCache';
import { type ArtworkRole, artworkDeliveryUrl, selectArtwork } from '../../utils/artwork';

interface ArtworkImageProps {
  artwork?: ReadonlyArray<ResolvedArtworkDto>;
  role?: ArtworkRole;
  alt?: string;
  style?: CSSProperties;
  fallback?: ReactNode;
  missingArtworkLabel?: ReactNode;
  priority?: boolean;
  onError?: () => void;
}

export function ArtworkImage({ artwork, role = 'Poster', alt = '', style, fallback, missingArtworkLabel, priority = false, onError }: ArtworkImageProps) {
  const element = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(200);
  const [visible, setVisible] = useState(typeof IntersectionObserver === 'undefined');
  const [account, setAccount] = useState<string | null>(null);
  const [image, setImage] = useState<{ key: string; url: string } | null>(null);
  const [loaded, setLoaded] = useState<string | null>(null);
  const [failed, setFailed] = useState<string | null>(null);
  const selected = selectArtwork(artwork, role, width, window.devicePixelRatio);
  const source = artworkDeliveryUrl(selected?.url, window.location.origin);
  const key = source && account && selected?.contentVersion
    ? artworkCacheKey(window.location.origin, account, selected.contentVersion, source) : source;

  useEffect(() => {
    if (!source) return;
    let active = true;
    const refresh = async () => {
      try {
        const user = await userManager.getUser();
        if (active) setAccount(user && !user.expired ? `${user.profile.iss}:${user.profile.sub}` : null);
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
  }, [source]);

  useEffect(() => {
    const target = element.current;
    if (!target) return;
    const measure = () => setWidth(target.getBoundingClientRect().width || 200);
    measure();
    const resize = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure);
    resize?.observe(target);
    const intersection = typeof IntersectionObserver === 'undefined' ? null : new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) setVisible(true);
    });
    intersection?.observe(target);
    return () => { resize?.disconnect(); intersection?.disconnect(); };
  }, []);

  useEffect(() => {
    if (role === 'Backdrop' || !source || !key || !account || !visible) return;
    const controller = new AbortController();
    let objectUrl: string | undefined;
    void loadRecentArtwork(source, key, controller.signal).then((blob) => {
      if (controller.signal.aborted || !blob) return;
      objectUrl = URL.createObjectURL(blob);
      setImage({ key, url: objectUrl });
    }).catch(() => undefined);
    return () => {
      controller.abort();
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [source, key, account, visible, role]);

  const url = image?.key === key ? image.url : source;
  const srcSet = role === 'Backdrop' ? artwork?.find(item => item.role === role)?.variants
    .filter(item => item.name.toLowerCase() !== 'original' && item.width > 0 && item.height > 0)
    .flatMap(item => { const safe = artworkDeliveryUrl(item.url, window.location.origin); return safe ? [`${safe} ${item.width}w`] : []; }).join(', ') : undefined;
  return (
    <div ref={element} style={{ position: 'relative', width: '100%', aspectRatio: role === 'Backdrop' ? '16 / 9' : role === 'Hero' ? '96 / 31' : role === 'Logo' ? '3 / 1' : '2 / 3', overflow: 'hidden', ...style }}>
      {(role !== 'Logo' || !url || loaded !== url || failed === url) && (
        <div style={{ position: role === 'Logo' ? 'relative' : 'absolute', minHeight: role === 'Logo' ? 'inherit' : undefined, inset: 0, display: 'grid', placeItems: role === 'Logo' ? 'var(--romd-logo-fallback-alignment, center start)' : 'center' }}>{fallback}</div>
      )}
      {url && failed !== url && (
        <img
          src={url}
          alt={alt}
          srcSet={srcSet || undefined}
          sizes={srcSet ? '100vw' : undefined}
          loading={priority ? 'eager' : 'lazy'}
          fetchPriority={priority ? 'high' : undefined}
          decoding="async"
          onLoad={() => setLoaded(url)}
          onError={() => { setFailed(url); onError?.(); }}
          style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', visibility: role === 'Logo' && loaded !== url ? 'hidden' : undefined, objectFit: role === 'Logo' ? 'contain' : selected?.fit ?? 'contain', objectPosition: role === 'Logo' ? 'var(--romd-logo-position, left center)' : `${selected?.focalX ?? 50}% ${selected?.focalY ?? 50}%` }}
        />
      )}
      {(!url || failed === url) && missingArtworkLabel}
    </div>
  );
}
