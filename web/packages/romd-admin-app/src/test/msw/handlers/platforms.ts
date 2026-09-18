import { HttpResponse, http } from 'msw';
import { datFixtures } from '../fixtures/dats';
import { platformFixtures } from '../fixtures/platforms';

export const platformHandlers = [
  // List all platforms
  http.get('/api/platforms', () => {
    return HttpResponse.json(platformFixtures.list);
  }),

  // Get platform by ID
  http.get('/api/platforms/:systemKey', ({ params }) => {
    const platform = platformFixtures.list.find((p) => p.id === params.systemKey);
    if (!platform) {
      return new HttpResponse(null, { status: 404 });
    }
    return HttpResponse.json(platform);
  }),

  // List DATs by platform
  http.get('/api/platforms/:systemKey/dats', ({ params }) => {
    const systemKey = params.systemKey as string;
    const dats = datFixtures.byPlatform[systemKey] ?? [];
    return HttpResponse.json(dats);
  }),

  // List titles by platform
  http.get('/api/platforms/:systemKey/titles', ({ params }) => {
    const platform = platformFixtures.list.find((p) => p.id === params.systemKey);
    if (!platform) {
      return new HttpResponse(null, { status: 404 });
    }

    return HttpResponse.json([
      {
        id: 'title-1',
        systemKey: params.systemKey,
        name: 'Super Mario World',
        hasLocalPayload: true,
        enrichmentStatus: 'Enriched',
        description: 'A classic platformer',
        publisher: 'Nintendo',
        developer: 'Nintendo EAD',
        genre: 'Platformer',
        releaseDate: '1991-08-13',
        players: '1-2',
        rating: '94',
        createdAt: '2024-01-15T10:00:00Z',
        lastEnrichedAt: '2024-01-15T12:00:00Z',
      },
    ]);
  }),

  // List titles with local payload by platform
  http.get('/api/platforms/:systemKey/local-payload', ({ params }) => {
    const platform = platformFixtures.list.find((p) => p.id === params.systemKey);
    if (!platform) {
      return new HttpResponse(null, { status: 404 });
    }

    return HttpResponse.json([
      {
        id: 'title-1',
        systemKey: params.systemKey,
        name: 'Super Mario World',
        hasLocalPayload: true,
        enrichmentStatus: 'Enriched',
      },
    ]);
  }),
];
