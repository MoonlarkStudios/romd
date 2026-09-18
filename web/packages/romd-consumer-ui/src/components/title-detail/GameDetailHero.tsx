import { Stack, Text, Title } from '@mantine/core';
import { type ReactNode, useState } from 'react';
import { ConsumerPresentation } from '../../ConsumerPresentation';

export interface GameDetailHeroProps {
  name: string;
  backdrop?: ReactNode;
  poster?: ReactNode;
  logoUrl?: string | null;
  metadata?: ReactNode;
  facts?: readonly (string | null | undefined)[];
  backAction?: ReactNode;
  actions?: ReactNode;
  releaseDetails?: ReactNode;
}

/** Data and actions belong to the host app; all visible composition belongs here. */
export function GameDetailHero({ name, backdrop, poster, logoUrl, metadata, facts = [], backAction, actions, releaseDetails }: GameDetailHeroProps) {
  const [failedLogo, setFailedLogo] = useState<string | null>(null);
  return <ConsumerPresentation>
      <section className="romd-detail-hero" data-has-backdrop={Boolean(backdrop) || undefined} aria-label={`${name} overview`}>
        {backdrop && <div className="romd-detail-backdrop">{backdrop}</div>}
        <div className="romd-detail-shade" />
        {backAction && <div className="romd-detail-back">{backAction}</div>}
        <div className="romd-detail-identity">
          {poster && <div className="romd-detail-poster">{poster}</div>}
          <Stack gap="lg" className="romd-detail-copy">
            <Title order={1} className="romd-hero" aria-label={name}>
              {logoUrl && failedLogo !== logoUrl ? <img className="romd-detail-logo" src={logoUrl} alt="" onError={() => setFailedLogo(logoUrl)} /> : name}
            </Title>
            {metadata ?? (facts.some(Boolean) && <Text className="romd-metadata">{facts.filter(Boolean).join(' · ')}</Text>)}
            {actions && <div className="romd-detail-actions">{actions}</div>}
            {releaseDetails && <Text className="romd-metadata">{releaseDetails}</Text>}
          </Stack>
        </div>
      </section>
  </ConsumerPresentation>;
}
