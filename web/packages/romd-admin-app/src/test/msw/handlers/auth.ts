import { HttpResponse, http } from 'msw';
import { authFixtures } from '../fixtures/auth';

export const authHandlers = [
  // Get current user
  http.get('/api/auth/me', ({ request }) => {
    const authHeader = request.headers.get('Authorization');

    if (!authHeader || !authHeader.startsWith('Bearer ')) {
      return new HttpResponse(null, { status: 401 });
    }

    return HttpResponse.json(authFixtures.currentUser);
  }),

  // Health check
  http.get('/health', () => {
    return HttpResponse.json({
      status: 'Healthy',
      totalDuration: '00:00:00.0123456',
      entries: {},
    });
  }),
];
