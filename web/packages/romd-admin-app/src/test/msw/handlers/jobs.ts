import { HttpResponse, http } from 'msw';
import { jobFixtures } from '../fixtures/jobs';

export const jobHandlers = [
  // List recent jobs
  http.get('/api/jobs', () => {
    return HttpResponse.json(jobFixtures.list);
  }),

  // Get job by ID
  http.get('/api/jobs/:id', ({ params }) => {
    const job = jobFixtures.list.find((j) => j.id === params.id);
    if (!job) {
      return new HttpResponse(null, { status: 404 });
    }
    return HttpResponse.json(job);
  }),

  // Archive job
  http.post('/api/jobs/:id/archive', ({ params }) => {
    const job = jobFixtures.list.find((j) => j.id === params.id);
    if (!job) {
      return new HttpResponse(null, { status: 404 });
    }
    if (!job.isTerminal) {
      return HttpResponse.json(
        {
          type: 'https://tools.ietf.org/html/rfc7231#section-6.5.1',
          title: 'Bad Request',
          status: 400,
          detail: 'Cannot archive a job that is not terminal',
        },
        { status: 400 }
      );
    }
    return new HttpResponse(null, { status: 204 });
  }),

  // Cancel job
  http.post('/api/jobs/:id/cancel', ({ params }) => {
    const job = jobFixtures.list.find((j) => j.id === params.id);
    if (!job) {
      return new HttpResponse(null, { status: 404 });
    }
    if (job.isTerminal) {
      return HttpResponse.json(
        {
          type: 'https://tools.ietf.org/html/rfc7231#section-6.5.1',
          title: 'Bad Request',
          status: 400,
          detail: 'Cannot cancel a terminal job',
        },
        { status: 400 }
      );
    }
    return new HttpResponse(null, { status: 204 });
  }),

  // Delete job
  http.delete('/api/jobs/:id', ({ params }) => {
    const job = jobFixtures.list.find((j) => j.id === params.id);
    if (!job) {
      return new HttpResponse(null, { status: 404 });
    }
    return new HttpResponse(null, { status: 204 });
  }),

  // Clear completed jobs
  http.post('/api/jobs/clear-completed', () => {
    return HttpResponse.json({ archivedCount: '2' });
  }),
];
