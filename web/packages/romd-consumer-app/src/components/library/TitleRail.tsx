import { ActionIcon, Anchor, Box, Group, Stack, Text, Title } from '@mantine/core';
import type {
  ConsumerCollectionTitleDto,
  ConsumerTitleCardDto,
  RecentlyPlayedTitleDto,
} from '@romd/consumer-api-client';
import { IconChevronLeft, IconChevronRight } from '@tabler/icons-react';
import { useEffect, useId, useRef, useState } from 'react';
import { Link } from 'react-router';
import { PosterCard } from './PosterCard';

type RailTitle = ConsumerTitleCardDto | ConsumerCollectionTitleDto | RecentlyPlayedTitleDto;

interface TitleRailProps {
  title: string;
  subtitle?: string | null;
  seeAllTo?: string;
  titles: RailTitle[];
  showPlayAction?: boolean;
}

export function TitleRail({ title, subtitle, seeAllTo, titles, showPlayAction = true }: TitleRailProps) {
  const railRef = useRef<HTMLDivElement | null>(null);

  const railId = useId();
  const [edges, setEdges] = useState({ left: false, right: false });

  useEffect(() => {
    const element = railRef.current;
    if (!element) return;
    const update = () => {
      const left = element.scrollLeft > 1;
      const right = element.scrollWidth - element.clientWidth - element.scrollLeft > 1;
      setEdges(previous => previous.left === left && previous.right === right ? previous : { left, right });
    };
    update();
    element.addEventListener('scroll', update, { passive: true });
    const observer = new ResizeObserver(update);
    observer.observe(element);
    for (const child of element.children) observer.observe(child);
    return () => {
      observer.disconnect();
      element.removeEventListener('scroll', update);
    };
  }, [titles.length]);

  if (titles.length === 0) {
    return null;
  }

  const scroll = (direction: 1 | -1) => {
    const element = railRef.current;
    if (!element) {
      return;
    }

    const first = element.children[0] as HTMLElement | undefined;
    const second = element.children[1] as HTMLElement | undefined;
    if (!first || !second) return;
    const stride = second.offsetLeft - first.offsetLeft;
    if (stride <= 0) return;
    const pageSize = Math.max(1, Math.floor(element.clientWidth / stride));
    const currentCard = direction > 0 ? Math.floor(element.scrollLeft / stride) : Math.ceil(element.scrollLeft / stride);
    const target = (currentCard + direction * pageSize) * stride;
    element.scrollTo({
      left: Math.max(0, Math.min(target, element.scrollWidth - element.clientWidth)),
      behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth',
    });
  };

  return (
    <Stack gap="xs">
      <Group
        align="baseline"
        gap="sm"
        wrap="nowrap"
      >
        <Title
          order={2}
          className="romd-section-heading"
        >
          {seeAllTo ? (
            <Anchor component={Link} to={seeAllTo} className="romd-shelf-heading-link">
              {title}
            </Anchor>
          ) : title}
        </Title>
        {subtitle && (
          <Text
            size="sm"
            c="dimmed"
            lineClamp={1}
          >
            {subtitle}
          </Text>
        )}
      </Group>

      <Box className="romd-rail-wrap">
        <RailArrow
          side="left"
          disabled={!edges.left}
          controls={railId}
          onClick={() => scroll(-1)}
        />
        <Box
          id={railId}
          data-left={edges.left || undefined}
          data-right={edges.right || undefined}
          ref={railRef}
        data-navigation-rail={seeAllTo ?? title}
          className="romd-rail"
        >
          {titles.map((railTitle) => (
            <PosterCard
              key={railTitle.id}
              title={railTitle}
              showPlayAction={showPlayAction}
            />
          ))}
        </Box>
        <RailArrow
          side="right"
          disabled={!edges.right}
          controls={railId}
          onClick={() => scroll(1)}
        />
      </Box>
    </Stack>
  );
}

interface RailArrowProps {
  side: 'left' | 'right';
  disabled: boolean;
  controls: string;
  onClick: () => void;
}

function RailArrow({ side, onClick, disabled, controls }: RailArrowProps) {
  return (
    <Box
      className="romd-rail-arrow"
      data-disabled={disabled || undefined}
      style={{
        position: 'absolute',
        top: 0,
        bottom: 28,
        [side]: 0,
        zIndex: 5,
        display: 'flex',
        alignItems: 'center',
        paddingInline: 4,

      }}
    >
      <ActionIcon
        disabled={disabled}
        aria-controls={controls}
        variant="subtle"
        color="gray"
        radius="xl"
        size="lg"
        aria-label={side === 'left' ? 'Scroll left' : 'Scroll right'}
        onClick={onClick}
      >
        {side === 'left' ? <IconChevronLeft size={18} /> : <IconChevronRight size={18} />}
      </ActionIcon>
    </Box>
  );
}
