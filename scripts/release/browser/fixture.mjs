import { createHash } from 'node:crypto';

export const fixtureName = 'ROMD Browser Qualification';

// Original, synthetic NROM-128: disable interrupts, set a background color and
// loop forever. Includes no commercial ROM, BIOS, artwork or external fixture.
export function cartridge() {
  const header = Buffer.from([0x4e, 0x45, 0x53, 0x1a, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
  const prg = Buffer.alloc(16 * 1024, 0xea);
  const program = [0x78, 0xd8, 0xa2, 0xff, 0x9a, 0xe8, 0x8e, 0x00, 0x20, 0x8e, 0x01,
    0x20, 0xa9, 0x3f, 0x8d, 0x06, 0x20, 0xa9, 0x00, 0x8d, 0x06, 0x20, 0xa9,
    0x2a, 0x8d, 0x07, 0x20];
  program.push(0x4c, program.length, 0x80);
  Buffer.from(program).copy(prg);
  for (const offset of [0x3ffa, 0x3ffc, 0x3ffe]) prg.writeUInt16LE(0x8000, offset);
  return Buffer.concat([header, prg, Buffer.alloc(8 * 1024)]);
}

export function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit += 1) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
  }
  return ((crc ^ 0xffffffff) >>> 0).toString(16).padStart(8, '0');
}

export function fixture() {
  const rom = cartridge();
  const digest = (name) => createHash(name).update(rom).digest('hex');
  return { rom, sha256: digest('sha256'), dat: Buffer.from(`<?xml version="1.0"?>
<datafile><header><name>${fixtureName}</name><description>Original disposable synthetic NROM</description><author>ROMD</author></header>
<game name="${fixtureName} (USA)"><rom name="qualification.nes" size="${rom.length}" crc="${crc32(rom)}" md5="${digest('md5')}" sha1="${digest('sha1')}"/></game></datafile>`) };
}
