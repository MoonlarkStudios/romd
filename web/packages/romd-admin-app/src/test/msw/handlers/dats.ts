import { HttpResponse, http } from 'msw';
import { datFixtures } from '../fixtures/dats';

export const datHandlers = [
  // List all DATs
  http.get('/api/dats', () => {
    return HttpResponse.json(datFixtures.list);
  }),

  // Get DAT by ID
  http.get('/api/dats/:datId', ({ params }) => {
    const dat = datFixtures.list.find((d) => d.id === params.datId);
    if (!dat) {
      return new HttpResponse(null, { status: 404 });
    }
    return HttpResponse.json(dat);
  }),

  // Assign platform to DAT
  http.post('/api/dats/:datId/platform', async ({ params, request }) => {
    const dat = datFixtures.list.find((d) => d.id === params.datId);
    if (!dat) {
      return new HttpResponse(null, { status: 404 });
    }

    const body = (await request.json()) as { systemKey: string };

    return HttpResponse.json({
      gamesUpdated: dat.gameCount ?? '0',
      newTitlesCreated: '10',
      existingTitlesMatched: '5',
    });
  }),

  // List games by DAT (paginated)
  http.get('/api/dats/:datId/games', ({ params }) => {
    const dat = datFixtures.list.find((d) => d.id === params.datId);
    if (!dat) {
      return new HttpResponse(null, { status: 404 });
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
  http.post('/api/dats/:datId/replace', () => {
    return HttpResponse.json(
      {
        jobId: 'job-new',
        backgroundJobId: 'bg-job-new',
        statusUrl: '/api/jobs/job-new',
      },
      { status: 202 }
    );
  }),
];
