import { describe, expect, it } from 'vitest';
import { classifyFile, getFileExtension, summarizeKinds } from './classify';

function file(name: string, type = ''): File {
  return new File(['x'], name, { type });
}

describe('getFileExtension', () => {
  it('returns the lowercased final extension', () => {
    expect(getFileExtension('Game.NES')).toBe('nes');
    expect(getFileExtension('archive.tar.gz')).toBe('gz');
  });

  it('returns empty string when there is no extension', () => {
    expect(getFileExtension('README')).toBe('');
  });
});

describe('classifyFile', () => {
  it('classifies archives by extension', () => {
    expect(classifyFile(file('roms.zip'))).toBe('archive');
    expect(classifyFile(file('roms.7z'))).toBe('archive');
  });

  it('classifies DAT catalogs by extension or xml mime', () => {
    expect(classifyFile(file('snes.dat'))).toBe('dat');
    expect(classifyFile(file('snes.xml'))).toBe('dat');
    expect(classifyFile(file('catalog', 'application/xml'))).toBe('dat');
  });

  it('classifies known ROM extensions', () => {
    expect(classifyFile(file('mario.nes'))).toBe('rom');
    expect(classifyFile(file('zelda.n64'))).toBe('rom');
    expect(classifyFile(file('sonic.bin'))).toBe('rom');
  });

  it('falls back to unknown for unrecognized files', () => {
    expect(classifyFile(file('notes.txt'))).toBe('unknown');
    expect(classifyFile(file('mystery'))).toBe('unknown');
  });
});

describe('summarizeKinds', () => {
  it('counts files by kind', () => {
    expect(
      summarizeKinds([
        { kind: 'rom' },
        { kind: 'rom' },
        { kind: 'dat' },
        { kind: 'archive' },
        { kind: 'unknown' },
      ]),
    ).toEqual({ rom: 2, dat: 1, archive: 1, unknown: 1 });
  });

  it('returns zeroes for an empty list', () => {
    expect(summarizeKinds([])).toEqual({ rom: 0, dat: 0, archive: 0, unknown: 0 });
  });
});
