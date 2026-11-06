import { HttpResponse, http } from 'msw';
import { datFixtures } from '../fixtures/dats';

export const datHandlers = [
  // List all DATs
  http.get('/api/dats', ({ request }) => {
    const unrouted = new URL(request.url).searchParams.get('unrouted');
    const items =
      unrouted === 'true' ? datFixtures.list.filter((dat) => !dat.systemKey) : datFixtures.list;
    return HttpResponse.json({
      items,
      nextCursor: null,
      hasNextPage: false,
    });
  }),

  // Get DAT by ID
  http.get('/api/dats/:datId', ({ params }) => {
    const dat = datFixtures.list.find((d) => d.id === params.datId);
    if (!dat) {
      return new HttpResponse(null, {
        status: 404,
      });
    }
    return HttpResponse.json(dat);
  }),

  // Assign platform to DAT
  http.patch('/api/dats/:datId/system', async ({ params, request }) => {
    const dat = datFixtures.list.find((d) => d.id === params.datId);
    if (!dat) {
      return new HttpResponse(null, {
        status: 404,
      });
    }

    await request.json();

    return HttpResponse.json({
      gamesUpdated: dat.gameCount ?? 0,
      newTitlesCreated: 10,
      existingTitlesMatched: 5,
    });
  }),

  // List games by DAT (paginated)
  http.get('/api/dats/:datId/games', ({ params }) => {
    const dat = datFixtures.list.find((d) => d.id === params.datId);
    if (!dat) {
      return new HttpResponse(null, {
        status: 404,
      });
    }

    return HttpResponse.json({
      items: [
        {
          id: 'game-1',
          datId: params.datId,
          name: 'Super Mario World (USA)',
          description: 'Super Mario World (USA)',
          year: '1991',
          manufacturer: 'Nintendo',
          roms: [],
          disks: [],
        },
      ],
      nextCursor: null,
      hasNextPage: false,
    });
  }),

  // Replace DAT
  http.post('/api/dats/:datId/replacements', () => {
    return HttpResponse.json(
      {
        jobId: 'job-new',
      },
      {
        status: 202,
      },
    );
  }),
];
