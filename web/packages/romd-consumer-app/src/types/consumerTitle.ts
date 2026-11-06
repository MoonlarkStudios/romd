import type {
  ConsumerMediaRefDto,
  ConsumerReleaseDto,
  ConsumerTitleDetailDto,
} from '@romd/consumer-api-client';

export type ConsumerTitleDetail = Omit<ConsumerTitleDetailDto, 'media' | 'releases'> & {
  media: ConsumerMediaRefDto[];
  releases: ConsumerReleaseDto[];
};
