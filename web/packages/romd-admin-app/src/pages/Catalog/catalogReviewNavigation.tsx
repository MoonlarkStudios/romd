import { Navigate, useParams, useSearchParams } from 'react-router';
import { type WorkbenchView, workbenchViews } from '../../components/Title/titleWorkbenchModel';

export function reviewState(value: string | null): WorkbenchView {
  return (
    workbenchViews.find((state) => state.toLowerCase() === value?.toLowerCase()) ?? 'NeedsReview'
  );
}

export function catalogReviewPath(state: WorkbenchView = 'NeedsReview', titleId?: string) {
  const params = new URLSearchParams({
    view: 'review',
  });
  if (state !== 'NeedsReview') params.set('state', state);
  if (titleId) params.set('title', titleId);
  return `/catalog?${params}`;
}

export function legacyEnrichmentTarget(
  params: URLSearchParams,
  {
    taskId,
    titleId,
  }: {
    taskId?: string;
    titleId?: string;
  },
) {
  const activity = taskId || params.get('activity');
  if (activity) return `/jobs/${encodeURIComponent(activity)}`;
  const target = new URLSearchParams({
    view: 'review',
  });
  if (params.has('view')) target.set('state', reviewState(params.get('view')));
  const title = titleId || params.get('title');
  if (title) target.set('title', title);
  for (const key of [
    'q',
    'role',
    'requirement',
  ]) {
    const value = params.get(key) ?? (key === 'requirement' ? params.get('issue') : null);
    if (value) target.set(key, value);
  }
  return `/catalog?${target}`;
}

export function LegacyEnrichmentRedirect() {
  const [params] = useSearchParams();
  const { titleId, taskId } = useParams();
  return (
    <Navigate
      to={legacyEnrichmentTarget(params, {
        taskId,
        titleId,
      })}
      replace
    />
  );
}
