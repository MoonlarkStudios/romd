import { Carousel } from '@mantine/carousel';
import { Box, Button, Group, Stack, Title, UnstyledButton, VisuallyHidden } from '@mantine/core';
import { useReducedMotion } from '@mantine/hooks';
import type { ConsumerTitleCardDto } from '@romd/consumer-api-client';
import { IconChevronLeft, IconChevronRight } from '@tabler/icons-react';
import type { EmblaCarouselType } from 'embla-carousel';
import { useState } from 'react';
import { TitleLink } from '../navigation/TitleLink';
import { ArtworkFallback } from './ArtworkFallback';
import { ArtworkImage } from './ArtworkImage';
import { SpotlightMetadata } from './SpotlightMetadata';

export function SpotlightCarousel({ titles }: { titles: ConsumerTitleCardDto[] }) {
  const [active, setActive] = useState(0);
  const [embla, setEmbla] = useState<EmblaCarouselType | null>(null);
  const reducedMotion = useReducedMotion();
  const multiple = titles.length > 1;
  return (
    <Box component="section" className="romd-spotlight-carousel" aria-label="Featured games" aria-roledescription="carousel">
      <Carousel
        getEmblaApi={setEmbla}
        onSlideChange={setActive}
        emblaOptions={{ loop: multiple, duration: reducedMotion ? 0 : undefined }}
        withControls={multiple}
        controlSize={44}
        nextControlIcon={<IconChevronRight size={24} />}
        previousControlIcon={<IconChevronLeft size={24} />}
        nextControlProps={{ 'aria-label': 'Next featured game' }}
        previousControlProps={{ 'aria-label': 'Previous featured game' }}
        classNames={{ container: 'romd-spotlight-slides', control: 'romd-spotlight-control', controls: 'romd-spotlight-controls' }}
      >
        {titles.map((title, index) => (
          <Carousel.Slide key={title.id} role="group" aria-roledescription="slide" aria-label={`${index + 1} of ${titles.length}: ${title.name}`} aria-hidden={index !== active} inert={index !== active}>
            <GameSpotlight title={title} />
          </Carousel.Slide>
        ))}
      </Carousel>
      {multiple && <div className="romd-spotlight-pagination" aria-label="Choose featured game">
        {titles.map((title, index) => <UnstyledButton key={title.id} className="romd-spotlight-indicator" aria-label={`Show ${title.name}`} aria-current={active === index ? 'true' : undefined} onClick={() => embla?.scrollTo(index)}><span /></UnstyledButton>)}
      </div>}
      <VisuallyHidden aria-live="polite" aria-atomic="true">{titles[active]?.name}, {active + 1} of {titles.length}</VisuallyHidden>
    </Box>
  );
}


function GameSpotlight({ title }: { title: ConsumerTitleCardDto }) {
  const hasHero = title.artwork?.some(item => item.role === 'Hero' && item.url);
  return (
    <Box  className="romd-home-spotlight" data-has-art={hasHero || undefined}>
      {hasHero && <Box className="romd-home-spotlight-art"><ArtworkImage artwork={title.artwork} role="Hero" alt="" style={{ height: '100%' }} /></Box>}
      <Box className="romd-home-spotlight-content">
        {!hasHero && <Box className="romd-home-spotlight-poster"><ArtworkImage artwork={title.artwork} fallback={<ArtworkFallback name={title.name} />} /></Box>}
        <Stack gap="md" className="romd-home-spotlight-info" style={{ minWidth: 0 }}>
          <Title order={1} className="romd-hero" aria-label={title.name}>
            {title.artwork?.some((item) => item.role === 'Logo') ? (
              <Box className="romd-home-spotlight-logo" aria-hidden="true">
                <ArtworkImage artwork={title.artwork} role="Logo" style={{ minHeight: 'inherit', aspectRatio: 'auto' }} fallback={<span>{title.name}</span>} />
              </Box>
            ) : title.name}
          </Title>
          <SpotlightMetadata title={title} />
          <Group gap="sm" mt="sm">
            <Button size="md" component={TitleLink} to={`/titles/${title.id}`} variant="default" className="romd-spotlight-details">View game</Button>
          </Group>
        </Stack>
      </Box>
    </Box>
  );
}
