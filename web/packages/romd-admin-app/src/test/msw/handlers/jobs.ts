import type { JobDto } from '@romd/admin-api-client';
import { HttpResponse, http } from 'msw';
import { jobFixtures } from '../fixtures/jobs';

let jobs: JobDto[] = structuredClone(jobFixtures.list);
export function resetJobHandlers() {
  jobs = structuredClone(jobFixtures.list);
}
const problem = (status: number, code: string, detail: string) =>
  HttpResponse.json(
    {
      status,
      code,
      title: detail,
      detail,
      traceId: 'test-trace',
    },
    {
      status,
      headers: {
        'Content-Type': 'application/problem+json',
      },
    },
  );

export const jobHandlers = [
  http.get('/api/admin/media/chd/reverification', () =>
    HttpResponse.json({
      available: false,
      decoderBuild: null,
      pendingCount: 0,
      maximumItems: 10000,
      activeJobId: null,
    }),
  ),
  http.get('/api/jobs', ({ request }) => {
    const query = new URL(request.url).searchParams;
    const archive = query.get('archive') ?? 'active';
    const filtered = jobs.filter(
      (job) =>
        (archive === 'all' || !!job.isArchived === (archive === 'archived')) &&
        (!query.get('jobType') || job.jobType === query.get('jobType')) &&
        (!query.get('correlationId') || job.correlationId === query.get('correlationId')),
    );
    return HttpResponse.json({
      items: filtered,
      nextCursor: null,
      hasNextPage: false,
    });
  }),
  http.get('/api/jobs/:jobId', ({ params }) => {
    const job = jobs.find((row) => row.id === params.jobId);
    return job ? HttpResponse.json(job) : problem(404, 'Jobs.NotFound', 'Job not found.');
  }),
  http.patch('/api/jobs/:jobId', async ({ params, request }) => {
    const index = jobs.findIndex((row) => row.id === params.jobId);
    const job = jobs[index];
    if (!job) return problem(404, 'Jobs.NotFound', 'Job not found.');
    if (!job.isTerminal)
      return problem(409, 'Jobs.NotTerminal', 'Only terminal jobs can be archived.');
    const body: unknown = await request.json();
    if (
      !body ||
      typeof body !== 'object' ||
      !('isArchived' in body) ||
      typeof body.isArchived !== 'boolean' ||
      Object.keys(body).some((key) => key !== 'isArchived')
    )
      return problem(400, 'Request.InvalidBody', 'Expected an archive state.');
    const updated = {
      ...job,
      isArchived: body.isArchived,
    };
    jobs[index] = updated;
    return HttpResponse.json(updated);
  }),
  http.post('/api/jobs/:jobId/cancel', ({ params }) => {
    const job = jobs.find((row) => row.id === params.jobId);
    if (!job) return problem(404, 'Jobs.NotFound', 'Job not found.');
    if (job.isTerminal) return problem(409, 'Jobs.AlreadyComplete', 'Job already complete.');
    if (job.jobType === 'metadata-rematerialization')
      return problem(409, 'Jobs.NotCancellable', 'This job cannot be cancelled.');
    return new HttpResponse(null, {
      status: 204,
    });
  }),
  http.delete('/api/jobs/:jobId', ({ params }) => {
    const job = jobs.find((row) => row.id === params.jobId);
    if (!job) return problem(404, 'Jobs.NotFound', 'Job not found.');
    return job.isArchived
      ? problem(501, 'Jobs.DeleteNotImplemented', 'Deletion is not implemented.')
      : problem(409, 'Jobs.NotArchived', 'Archive the job before deleting it.');
  }),
  http.post('/api/jobs/clear-completed', () =>
    HttpResponse.json({
      archivedCount: 2,
    }),
  ),
];
