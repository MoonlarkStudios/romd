import { HttpResponse, http } from 'msw';
import { operationalDiagnosticsFixture } from '../fixtures/diagnostics';

export const diagnosticsHandlers = [
  http.get('*/api/diagnostics/operational', () => {
    return HttpResponse.json(operationalDiagnosticsFixture);
  }),
];
