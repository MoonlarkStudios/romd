import type { ConsumerTitleCard } from '@romd/consumer-api-client';
import { GameMetadata } from '@romd/consumer-ui';
import '@romd/consumer-ui/styles.css';

export function SpotlightMetadata({ title }: { title: ConsumerTitleCard }) {
  return <GameMetadata {...title} />;
}
