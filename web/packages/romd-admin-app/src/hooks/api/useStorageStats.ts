import type { StorageStatsDto } from '@romd/admin-api-client';
import { getStorageStats } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';

export interface StorageCategory {
  category: string;
  fileCount: number;
  sizeBytes: string;
  sizeOnDiskBytes: string;
}

export interface StorageStats {
  totalStorageBytes: string;
  totalStorageBytesOnDisk: string;
  compressedFileCount: number;
  uncompressedFileCount: number;
  averageCompressionRatio: number;
  bytesSaved: string;
  breakdown: StorageCategory[];
}

export const storageStatsKeys = {
  all: ['dashboard', 'storage'] as const,
};

const mapStorageStats = (dto: StorageStatsDto): StorageStats => ({
  totalStorageBytes: dto.totalStorageBytes,
  totalStorageBytesOnDisk: dto.totalStorageBytesOnDisk,
  compressedFileCount: dto.compressedFileCount,
  uncompressedFileCount: dto.uncompressedFileCount,
  averageCompressionRatio: dto.averageCompressionRatio,
  bytesSaved: dto.bytesSaved,
  breakdown: (dto.breakdown ?? []).map((category) => ({
    category: category.category,
    fileCount: category.fileCount,
    sizeBytes: category.sizeBytes,
    sizeOnDiskBytes: category.sizeOnDiskBytes,
  })),
});

export function useStorageStats() {
  return useQuery({
    queryKey: storageStatsKeys.all,
    queryFn: async (): Promise<StorageStats> => {
      const response = await getStorageStats();
      if (response.error || !response.data) {
        throw new Error('Failed to fetch storage stats');
      }
      return mapStorageStats(response.data as StorageStatsDto);
    },
    staleTime: Number.POSITIVE_INFINITY,
  });
}
