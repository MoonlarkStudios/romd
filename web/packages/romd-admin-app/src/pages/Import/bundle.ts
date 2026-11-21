import { Zip, ZipPassThrough } from 'fflate';

/**
 * Bundling builds the whole zip in memory (see zipFiles), so we only do it for
 * small, loose multi-file drops. Above this combined size we skip the in-browser
 * zip and upload each file as its own job instead.
 */
export const MAX_BUNDLE_BYTES = 512 * 1024 * 1024; // 512 MB total (zip output is held in memory)

/** A single file larger than this is uploaded on its own rather than bundled. */
export const MAX_BUNDLE_FILE_BYTES = 256 * 1024 * 1024; // 256 MB

/**
 * The plan for turning a staged drop into upload requests. The backend model is
 * one file per POST → one job, so we bundle loose/folder drops into a single
 * archive to keep them as ONE job with rich extraction progress.
 */
export type BundlePlan =
  /** A single dropped file (incl. an archive/DAT) — upload as-is. */
  | {
      kind: 'single';
      file: File;
    }
  /** Many files zipped (STORE level) into one archive — one job. */
  | {
      kind: 'bundled';
      file: File;
    }
  /** Too large to safely zip in-browser — upload each file as its own job. */
  | {
      kind: 'individual';
      files: File[];
    };

function totalBytes(files: readonly File[]): number {
  return files.reduce((sum, file) => sum + file.size, 0);
}

// Already-compressed containers gain nothing from a STORE-level re-zip (the server extracts
// and the CAS de-dups), and re-zipping them in memory is the worst case — never bundle these.
const ARCHIVE_EXTENSIONS = [
  '.zip',
  '.7z',
  '.rar',
  '.tar',
  '.gz',
  '.tgz',
  '.chd',
  '.cso',
  '.rvz',
  '.wbfs',
  '.gcz',
];

function isArchive(file: File): boolean {
  const name = file.name.toLowerCase();
  return ARCHIVE_EXTENSIONS.some((ext) => name.endsWith(ext));
}

/** Preserve folder structure (webkitRelativePath) and de-duplicate entry names. */
function entryNames(files: File[]): string[] {
  const used = new Set<string>();
  return files.map((file) => {
    const base = file.webkitRelativePath || file.name;
    let name = base;
    let counter = 1;
    while (used.has(name)) {
      const dot = base.lastIndexOf('.');
      name =
        dot > 0 ? `${base.slice(0, dot)} (${counter})${base.slice(dot)}` : `${base} (${counter})`;
      counter += 1;
    }
    used.add(name);
    return name;
  });
}

/**
 * Streams each input file chunk-by-chunk into a single STORE-level (uncompressed)
 * zip. NOTE: the resulting zip is collected in memory before upload, so callers must
 * keep bundles small (see MAX_BUNDLE_BYTES / MAX_BUNDLE_FILE_BYTES). CAS deduplicates
 * on the server, so compressing here would only burn CPU.
 */
async function zipFiles(files: File[], signal?: AbortSignal): Promise<File> {
  const chunks: Uint8Array[] = [];
  let failure: Error | null = null;

  const zip = new Zip((err, chunk, _final) => {
    if (err) {
      failure = err;
      return;
    }
    if (chunk.length > 0) chunks.push(chunk);
  });

  const names = entryNames(files);

  for (let index = 0; index < files.length; index += 1) {
    signal?.throwIfAborted();
    const entry = new ZipPassThrough(names[index]);
    zip.add(entry);

    const reader = files[index].stream().getReader();
    for (;;) {
      if (signal?.aborted) {
        await reader.cancel();
        signal.throwIfAborted();
      }
      const { done, value } = await reader.read();
      if (failure) throw failure;
      if (done) break;
      if (value) entry.push(value, false);
    }
    entry.push(new Uint8Array(0), true);
    if (failure) throw failure;
  }

  zip.end();
  if (failure) throw failure;

  const blob = new Blob(chunks as BlobPart[], {
    type: 'application/zip',
  });
  return new File(
    [
      blob,
    ],
    `import-${Date.now()}.zip`,
    {
      type: 'application/zip',
    },
  );
}

export function uploadPlanKind(files: readonly File[]): BundlePlan['kind'] {
  if (files.length <= 1) return 'single';

  // Large drops and archives already on disk are transferred individually to avoid a second copy in memory.
  return totalBytes(files) > MAX_BUNDLE_BYTES ||
    files.some((file) => file.size > MAX_BUNDLE_FILE_BYTES || isArchive(file))
    ? 'individual'
    : 'bundled';
}

export async function bundleForUpload(files: File[], signal?: AbortSignal): Promise<BundlePlan> {
  signal?.throwIfAborted();
  if (files.length === 0) throw new Error('No files to upload.');

  const kind = uploadPlanKind(files);
  if (kind === 'single')
    return {
      kind,
      file: files[0],
    };
  if (kind === 'individual')
    return {
      kind,
      files,
    };
  return {
    kind,
    file: await zipFiles(files, signal),
  };
}
