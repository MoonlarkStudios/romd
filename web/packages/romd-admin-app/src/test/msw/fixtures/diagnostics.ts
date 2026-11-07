import type { DashboardStatusBar, OperationalDiagnostics } from '@romd/admin-api-client';

export const dashboardStatusBarFixture = {
  storage: {
    totalStorageBytes: '5636096',
    totalStorageBytesOnDisk: '4194304',
    compressedFileCount: 3,
    uncompressedFileCount: 4,
    averageCompressionRatio: 0.74,
    bytesSaved: '1441792',
    breakdown: [
      {
        category: 'ROMs',
        fileCount: 7,
        sizeBytes: '5636096',
        sizeOnDiskBytes: '4194304',
      },
    ],
  },
  coverage: {
    expectedTitleCount: 3200,
    localPayloadTitleCount: 5,
    completeTitleCount: 4,
    partialTitleCount: 1,
    coverageHealthPercent: 0.125,
    lastDatRefreshAt: '2026-08-26T17:55:00Z',
  },
  health: {
    totalRoms: 7,
    identifiedCount: 5,
    unidentifiedCount: 2,
    routedCount: 4,
    unroutedCount: 1,
    failedJobCount: 0,
  },
} satisfies DashboardStatusBar;

export const operationalDiagnosticsFixture = {
  generatedAt: '2026-08-26T18:00:00Z',
  catalogProjections: {
    availability: 'Available',
    error: null,
    items: [
      {
        systemKey: 'snes',
        platformName: 'Super Nintendo Entertainment System',
        platformShortName: 'SNES',
        state: 'Clean',
        rebuiltAt: '2026-08-26T17:55:00Z',
        lastError: null,
        failedAt: null,
      },
      {
        systemKey: 'genesis',
        platformName: 'Sega Genesis',
        platformShortName: 'Genesis',
        state: 'Failed',
        rebuiltAt: '2026-08-25T12:00:00Z',
        lastError: 'Projection rebuild stopped at release 42.',
        failedAt: '2026-08-26T17:30:00Z',
      },
    ],
    isTruncated: false,
  },
  jobs: {
    availability: 'Available',
    error: null,
    wedgedReplaceDatJobs: [
      {
        jobId: 'replace-job-1',
        existingDatId: 'dat-1',
        systemKey: 'genesis',
        phase: 'Replacing',
        createdAt: '2026-08-26T16:00:00Z',
        startedAt: '2026-08-26T16:02:00Z',
        lastProgressAt: '2026-08-26T16:20:00Z',
        stalledForSeconds: 6000,
        attemptError: 'The provider did not acknowledge the replacement.',
        attemptErrorTruncated: false,
      },
    ],
    wedgedReplaceDatJobsTruncated: false,
    strandedBulkEnrichmentJobs: [
      {
        jobId: 'enrichment-job-1',
        systemKey: 'snes',
        platformLabel: 'SNES',
        phase: 'Pending',
        createdAt: '2026-08-26T17:20:00Z',
        strandedForSeconds: 2400,
      },
    ],
    strandedBulkEnrichmentJobsTruncated: false,
  },
  outbox: {
    availability: 'Available',
    error: null,
    pendingCount: 12,
    oldestPendingAgeSeconds: 480,
    failingCount: 2,
    lastProcessedAt: '2026-08-26T17:52:00Z',
  },
  hangfire: {
    availability: 'Available',
    error: null,
    servers: [
      {
        name: 'worker-1',
        workerCount: 8,
        queues: [
          'default',
          'upload',
          'enrichment',
        ],
        startedAt: '2026-08-26T12:00:00Z',
        heartbeatAt: '2026-08-26T17:59:45Z',
        heartbeatAgeSeconds: 15,
      },
    ],
    serversTruncated: false,
    recurringJobs: [
      {
        id: 'dat-replacement-convergence-sweep',
        queue: 'default',
        cron: '*/10 * * * *',
        lastExecutionAt: '2026-08-26T17:50:00Z',
        nextExecutionAt: '2026-08-26T18:00:00Z',
        lastJobState: 'Succeeded',
        error: null,
      },
    ],
    recurringJobsTruncated: false,
    queueBacklogs: [
      {
        queue: 'default',
        enqueuedCount: '1',
        fetchedCount: '0',
      },
      {
        queue: 'upload',
        enqueuedCount: '3',
        fetchedCount: '1',
      },
    ],
  },
  storage: {
    dataVolumeAvailability: 'Available',
    dataVolumeFreeBytes: '53687091200',
    casAvailability: 'Available',
    error: null,
  },
} satisfies OperationalDiagnostics;

export const emptyOperationalDiagnosticsFixture = {
  ...operationalDiagnosticsFixture,
  catalogProjections: {
    ...operationalDiagnosticsFixture.catalogProjections,
    items: [],
  },
  jobs: {
    ...operationalDiagnosticsFixture.jobs,
    wedgedReplaceDatJobs: [],
    strandedBulkEnrichmentJobs: [],
  },
  outbox: {
    ...operationalDiagnosticsFixture.outbox,
    pendingCount: 0,
    oldestPendingAgeSeconds: null,
    failingCount: 0,
  },
  hangfire: {
    ...operationalDiagnosticsFixture.hangfire,
    servers: [],
    recurringJobs: [],
    queueBacklogs: [],
  },
} satisfies OperationalDiagnostics;
