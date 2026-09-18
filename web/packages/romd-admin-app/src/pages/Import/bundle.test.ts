import { beforeEach, describe, expect, it, vi } from 'vitest';

// Mock fflate's streaming zip so the test exercises our orchestration
// (read each file → push into an entry → collect chunks → wrap in a File)
// without depending on a real compressor or jsdom's Blob internals.
vi.mock('fflate', () => {
  class ZipPassThrough {
    filename: string;
    // biome-ignore lint/suspicious/noExplicitAny: test stub mirrors fflate's loose handler type
    ondata: ((err: unknown, chunk: Uint8Array, final: boolean) => void) | null = null;
    constructor(filename: string) {
      this.filename = filename;
    }
    push(chunk: Uint8Array, final: boolean) {
      this.ondata?.(null, chunk, final);
    }
  }
  class Zip {
    cb: (err: unknown, chunk: Uint8Array, final: boolean) => void;
    constructor(cb: (err: unknown, chunk: Uint8Array, final: boolean) => void) {
      this.cb = cb;
    }
    add(entry: ZipPassThrough) {
      entry.ondata = (err, chunk, final) => this.cb(err, chunk, final);
    }
    end() {
      this.cb(null, new Uint8Array([80, 75, 5, 6]), true);
    }
  }
  return { Zip, ZipPassThrough };
});

import { bundleForUpload, MAX_BUNDLE_BYTES, MAX_BUNDLE_FILE_BYTES } from './bundle';

/** A File whose stream() yields its bytes (jsdom does not implement Blob.stream). */
function streamableFile(name: string, content = 'data', size?: number): File {
  const bytes = new TextEncoder().encode(content);
  const f = new File([content], name);
  Object.defineProperty(f, 'stream', {
    value: () =>
      new ReadableStream<Uint8Array>({
        start(controller) {
          controller.enqueue(bytes);
          controller.close();
        },
      }),
  });
  if (size !== undefined) {
    Object.defineProperty(f, 'size', { value: size });
  }
  return f;
}

describe('bundleForUpload', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it('throws when there are no files', async () => {
    await expect(bundleForUpload([])).rejects.toThrow();
  });

  it('passes a single file through without bundling', async () => {
    const only = streamableFile('roms.zip');
    const plan = await bundleForUpload([only]);
    expect(plan).toEqual({ kind: 'single', file: only });
  });

  it('bundles multiple files into one zip File', async () => {
    const plan = await bundleForUpload([
      streamableFile('a.nes'),
      streamableFile('b.nes'),
    ]);
    expect(plan.kind).toBe('bundled');
    if (plan.kind !== 'bundled') throw new Error('expected bundled');
    expect(plan.file).toBeInstanceOf(File);
    expect(plan.file.name).toMatch(/^import-\d+\.zip$/);
    expect(plan.file.type).toBe('application/zip');
    expect(plan.file.size).toBeGreaterThan(0);
  });

  it('uploads files individually when the drop exceeds the bundle ceiling', async () => {
    const big = MAX_BUNDLE_BYTES;
    const files = [streamableFile('huge1.iso', 'x', big), streamableFile('huge2.iso', 'x', 1)];
    const plan = await bundleForUpload(files);
    expect(plan).toEqual({ kind: 'individual', files });
  });

  it('skips bundling when the drop already contains an archive', async () => {
    const files = [streamableFile('a.nes'), streamableFile('roms.zip')];
    const plan = await bundleForUpload(files);
    expect(plan).toEqual({ kind: 'individual', files });
  });

  it('skips bundling when a single file exceeds the per-file cap, even under the total', async () => {
    const files = [
      streamableFile('big.bin', 'x', MAX_BUNDLE_FILE_BYTES + 1),
      streamableFile('small.nes', 'x', 1),
    ];
    const plan = await bundleForUpload(files);
    expect(plan).toEqual({ kind: 'individual', files });
  });
});
