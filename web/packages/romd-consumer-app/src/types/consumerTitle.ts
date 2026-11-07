import type {
  ConsumerMediaRef,
  ConsumerRelease,
  ConsumerTitleDetail as ConsumerTitleDetailContract,
} from '@romd/consumer-api-client';

export type ConsumerTitleDetail = Omit<ConsumerTitleDetailContract, 'media' | 'releases'> & {
  media: ConsumerMediaRef[];
  releases: ConsumerRelease[];
};
