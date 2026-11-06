import { HttpResponse, http } from 'msw';
import { dashboardStatusBarFixture, operationalDiagnosticsFixture } from '../fixtures/diagnostics';

export const diagnosticsHandlers = [
  http.get('*/api/dashboard/status-bar', () => {
    return HttpResponse.json(dashboardStatusBarFixture);
  }),
  http.get('*/api/server/diagnostics', () => {
    return HttpResponse.json(operationalDiagnosticsFixture);
  }),
];
