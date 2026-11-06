import { HttpResponse, http } from 'msw';

export const uploadHandlers = [
  // Generic upload
  http.post('/api/upload', () => {
    return HttpResponse.json(
      {
        jobId: 'job-new-upload',
      },
      {
        status: 202,
      },
    );
  }),

  // Upload DAT file
  http.post('/api/upload/dat', () => {
    return HttpResponse.json(
      {
        jobId: 'job-new-dat',
      },
      {
        status: 202,
      },
    );
  }),

  // Upload ROM file
  http.post('/api/upload/rom', () => {
    return HttpResponse.json(
      {
        jobId: 'job-new-rom',
      },
      {
        status: 202,
      },
    );
  }),
];
