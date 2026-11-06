import type {
  JobDtoMediaReconciliationJobDto,
  MediaReconciliationSelectionDto,
  MediaReconciliationSubjectDto,
} from '@romd/admin-api-client';

type SubjectJob = JobDtoMediaReconciliationJobDto & {
  jobType: 'media-reconciliation';
  requestMode: 'Subject';
  subject: MediaReconciliationSubjectDto;
  selection: null;
};
type SelectionJob = JobDtoMediaReconciliationJobDto & {
  jobType: 'media-reconciliation';
  requestMode: 'Selection';
  subject: null;
  selection: MediaReconciliationSelectionDto;
};

export function subjectReconciliationJob(overrides: Partial<SubjectJob> = {}): SubjectJob {
  return {
    jobType: 'media-reconciliation',
    requestMode: 'Subject',
    subject: {
      kind: 'LogicalRom',
      id: 'ROMsqid',
    },
    selection: null,
    id: '00000000-0000-0000-0000-000000000001',
    correlationId: '00000000-0000-0000-0000-000000000001',
    sourceFilename: 'Luigi.iso',
    phase: 'Encoding',
    progressPercent: 25,
    errors: [],
    isTerminal: false,
    hasErrors: false,
    ...overrides,
  };
}

export function selectionReconciliationJob(overrides: Partial<SelectionJob> = {}): SelectionJob {
  return {
    jobType: 'media-reconciliation',
    requestMode: 'Selection',
    subject: null,
    id: '00000000-0000-0000-0000-000000000002',
    correlationId: '00000000-0000-0000-0000-000000000002',
    sourceFilename: 'Media selection',
    phase: 'Waiting',
    progressPercent: 25,
    errors: [],
    isTerminal: false,
    hasErrors: true,
    selection: {
      filter: 'AllSubjects',
      limit: 100,
      conversionRequested: false,
      selected: 8,
      created: 4,
      adopted: 2,
      skipped: 2,
      completed: 3,
      failed: 1,
      enumerationComplete: true,
    },
    ...overrides,
  };
}
