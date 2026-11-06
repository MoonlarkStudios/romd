import { HttpResponse, http } from 'msw';
import { romFixtures } from '../fixtures/roms';

const DEFAULT_LIMIT = 50;

export const romHandlers = [
  http.get('/api/roms', ({ request }) => {
    const url = new URL(request.url);
    const status = url.searchParams.get('status');
    const query = url.searchParams.get('query')?.toLowerCase() ?? '';
    const items = romFixtures.list.filter(
      (rom) =>
        (!status || rom.status?.toLowerCase() === status) &&
        rom.originalFilename.toLowerCase().includes(query),
    );
    return HttpResponse.json(
      romFixtures.paginate(
        items,
        url.searchParams.get('cursor'),
        Number(url.searchParams.get('limit') ?? DEFAULT_LIMIT),
      ),
    );
  }),
  http.get('/api/roms/stats', () => HttpResponse.json(romFixtures.stats)),

  // Get ROM by ID
  http.get('/api/roms/:romId', ({ params }) => {
    const rom = romFixtures.list.find((r) => r.id === params.romId);
    if (!rom) {
      return new HttpResponse(null, {
        status: 404,
      });
    }
    return HttpResponse.json(rom);
  }),

  // Delete ROM
  http.delete('/api/roms/:romId', ({ params }) => {
    const rom = romFixtures.list.find((r) => r.id === params.romId);
    if (!rom) {
      return new HttpResponse(null, {
        status: 404,
      });
    }
    return new HttpResponse(null, {
      status: 204,
    });
  }),

  // Download ROM
  http.get('/api/roms/:romId/download', ({ params }) => {
    const rom = romFixtures.list.find((r) => r.id === params.romId);
    if (!rom) {
      return new HttpResponse(null, {
        status: 404,
      });
    }
    return new HttpResponse(
      new Blob([
        'mock rom data',
      ]),
      {
        headers: {
          'Content-Type': 'application/octet-stream',
          'Content-Disposition': `attachment; filename="${rom.originalFilename}"`,
        },
      },
    );
  }),
];
