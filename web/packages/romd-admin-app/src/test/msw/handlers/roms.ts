import { HttpResponse, http } from 'msw';
import { romFixtures } from '../fixtures/roms';

const DEFAULT_LIMIT = 50;

export const romHandlers = [
  // List all ROMs
  http.get('/api/roms', () => {
    return HttpResponse.json(romFixtures.list);
  }),

  // Get library stats
  http.get('/api/roms/stats', () => {
    return HttpResponse.json(romFixtures.stats);
  }),

  // List cataloged ROMs (paginated)
  http.get('/api/roms/cataloged', ({ request }) => {
    const url = new URL(request.url);
    const cursor = url.searchParams.get('cursor');
    const limit = Number(url.searchParams.get('limit')) || DEFAULT_LIMIT;

    const page = romFixtures.paginate(romFixtures.cataloged, cursor, limit);
    return HttpResponse.json(page);
  }),

  // List unidentified ROMs (paginated)
  http.get('/api/roms/unidentified', ({ request }) => {
    const url = new URL(request.url);
    const cursor = url.searchParams.get('cursor');
    const limit = Number(url.searchParams.get('limit')) || DEFAULT_LIMIT;

    const page = romFixtures.paginate(romFixtures.unidentified, cursor, limit);
    return HttpResponse.json(page);
  }),

  // List unrouted ROMs (paginated)
  http.get('/api/roms/unrouted', ({ request }) => {
    const url = new URL(request.url);
    const cursor = url.searchParams.get('cursor');
    const limit = Number(url.searchParams.get('limit')) || DEFAULT_LIMIT;

    const page = romFixtures.paginate(romFixtures.unrouted, cursor, limit);
    return HttpResponse.json(page);
  }),

  // Get ROM by ID
  http.get('/api/roms/:romId', ({ params }) => {
    const rom = romFixtures.list.find((r) => r.id === params.romId);
    if (!rom) {
      return new HttpResponse(null, { status: 404 });
    }
    return HttpResponse.json(rom);
  }),

  // Delete ROM
  http.delete('/api/roms/:romId', ({ params }) => {
    const rom = romFixtures.list.find((r) => r.id === params.romId);
    if (!rom) {
      return new HttpResponse(null, { status: 404 });
    }
    return new HttpResponse(null, { status: 204 });
  }),

  // Download ROM
  http.get('/api/roms/:romId/download', ({ params }) => {
    const rom = romFixtures.list.find((r) => r.id === params.romId);
    if (!rom) {
      return new HttpResponse(null, { status: 404 });
    }
    return new HttpResponse(new Blob(['mock rom data']), {
      headers: {
        'Content-Type': 'application/octet-stream',
        'Content-Disposition': `attachment; filename="${rom.originalFilename}"`,
      },
    });
  }),
];
