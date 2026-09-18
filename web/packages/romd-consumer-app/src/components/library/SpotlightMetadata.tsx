import type { ConsumerTitleCardDto } from '@romd/consumer-api-client';
import { GameMetadata } from '@romd/consumer-ui';
import '@romd/consumer-ui/styles.css';

export function SpotlightMetadata({ title }: { title: ConsumerTitleCardDto }) {
  return <GameMetadata {...title} />;
}
