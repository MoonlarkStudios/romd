import { HttpResponse, http } from 'msw';
import { datFixtures } from '../fixtures/dats';
import { platformFixtures } from '../fixtures/platforms';

export const platformHandlers = [
  // List all platforms
  http.get('/api/systems', ({ request }) => {
    const query = new URL(request.url).searchParams;
    const offset = Number(query.get('cursor') ?? '0');
    const limit = Number(query.get('limit') ?? '100');
    const items = platformFixtures.list.slice(offset, offset + limit);
    const nextCursor =
      offset + limit < platformFixtures.list.length ? String(offset + limit) : null;
    return HttpResponse.json({
      items,
      nextCursor,
      hasNextPage: nextCursor !== null,
    });
  }),

  // Get platform by ID
  http.get('/api/systems/:systemKey', ({ params }) => {
    const platform = platformFixtures.list.find((p) => p.key === params.systemKey);
    if (!platform) {
      return new HttpResponse(null, {
        status: 404,
      });
    }
    return HttpResponse.json(platform);
  }),

  // List DATs by platform
  http.get('/api/systems/:systemKey/dats', ({ params }) => {
    const systemKey = params.systemKey as string;
    const dats = datFixtures.byPlatform[systemKey] ?? [];
    return HttpResponse.json({
      items: dats,
      nextCursor: null,
      hasNextPage: false,
    });
  }),
];
