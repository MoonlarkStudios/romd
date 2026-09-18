/**
 * Client-side, best-effort file classification used to preview a staged drop
 * before upload. The backend performs the authoritative classification
 * (magic bytes + content sniffing) during the upload job — this only powers
 * the staging summary so the user can see roughly what they're about to send.
 */
export type FileKind = 'rom' | 'archive' | 'dat' | 'unknown';

const archiveExtensions = new Set(['7z', 'zip', 'rar', 'tar', 'gz', 'tgz']);

const catalogExtensions = new Set(['dat', 'xml']);

const romExtensions = new Set([
  'a26',
  'a52',
  'a78',
  'bin',
  'chd',
  'col',
  'cue',
  'gb',
  'gba',
  'gbc',
  'gen',
  'gg',
  'int',
  'iso',
  'lnx',
  'md',
  'n64',
  'nds',
  'nes',
  'ngc',
  'ngp',
  'pce',
  'sfc',
  'smc',
  'sms',
  'v64',
  'vb',
  'vec',
  'ws',
  'wsc',
  'z64',
]);

export function getFileExtension(fileName: string): string {
  const lower = fileName.toLowerCase();
  const dot = lower.lastIndexOf('.');
  return dot > 0 ? lower.slice(dot + 1) : '';
}

export function classifyFile(file: File): FileKind {
  const extension = getFileExtension(file.name);
  if (archiveExtensions.has(extension)) return 'archive';
  if (
    catalogExtensions.has(extension) ||
    file.type === 'text/xml' ||
    file.type === 'application/xml'
  ) {
    return 'dat';
  }
  if (romExtensions.has(extension)) return 'rom';
  return 'unknown';
}

export interface StagingSummary {
  rom: number;
  archive: number;
  dat: number;
  unknown: number;
}

export function summarizeKinds(files: { kind: FileKind }[]): StagingSummary {
  return files.reduce<StagingSummary>(
    (acc, { kind }) => {
      acc[kind] += 1;
      return acc;
    },
    { rom: 0, archive: 0, dat: 0, unknown: 0 },
  );
}
