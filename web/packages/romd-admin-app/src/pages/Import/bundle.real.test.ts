import { strFromU8, unzipSync } from 'fflate';
import { describe, expect, it } from 'vitest';
import { bundleForUpload } from './bundle';

// No vi.mock here — this exercises the REAL fflate streaming API, which is the
// load-bearing mechanism behind client-side bundling. A misuse of the streaming
// API would produce a corrupt archive that tsc and the mocked test can't catch.

// jsdom does not implement File.arrayBuffer(); read produced bytes via FileReader
// (prod never reads the File back — it streams it through fetch/FormData).
function readBytes(file: File): Promise<Uint8Array> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(new Uint8Array(reader.result as ArrayBuffer));
    reader.onerror = () => reject(reader.error);
    reader.readAsArrayBuffer(file);
  });
}

function streamableFile(name: string, content: string, relativePath?: string): File {
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
  if (relativePath) {
    Object.defineProperty(f, 'webkitRelativePath', { value: relativePath });
  }
  return f;
}

describe('bundleForUpload (real fflate)', () => {
  it('produces a zip that round-trips every entry and its bytes', async () => {
    const plan = await bundleForUpload([
      streamableFile('mario.nes', 'MARIO_ROM_BYTES'),
      streamableFile('zelda.nes', 'ZELDA_ROM_BYTES'),
    ]);

    if (plan.kind !== 'bundled') throw new Error('expected a bundled plan');

    const bytes = await readBytes(plan.file);
    const unzipped = unzipSync(bytes);

    expect(Object.keys(unzipped).sort()).toEqual(['mario.nes', 'zelda.nes']);
    expect(strFromU8(unzipped['mario.nes'])).toBe('MARIO_ROM_BYTES');
    expect(strFromU8(unzipped['zelda.nes'])).toBe('ZELDA_ROM_BYTES');
  });

  it('preserves folder paths and de-duplicates colliding names', async () => {
    const plan = await bundleForUpload([
      streamableFile('game.bin', 'A', 'snes/game.bin'),
      streamableFile('game.bin', 'B', 'genesis/game.bin'),
      streamableFile('game.bin', 'C'),
    ]);

    if (plan.kind !== 'bundled') throw new Error('expected a bundled plan');

    const unzipped = unzipSync(await readBytes(plan.file));
    const names = Object.keys(unzipped).sort();

    // Folder-relative paths preserved; the third (bare, colliding) name is disambiguated.
    expect(names).toContain('snes/game.bin');
    expect(names).toContain('genesis/game.bin');
    expect(names).toContain('game.bin');
    expect(names.length).toBe(3);
  });
});
