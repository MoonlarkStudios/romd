import { forwardRef, useContext } from 'react';
import { Link, type LinkProps, useLocation, useNavigate } from 'react-router';
import { CollectionNavigationName, captureReturnScroll, titleOrigin } from './titleNavigation';

/** Canonical title/play URLs with per-entry return context. Modified clicks remain normal links. */
export const TitleLink = forwardRef<HTMLAnchorElement, LinkProps>(function TitleLink({ onClick, state, ...props }, ref) {
  const location = useLocation();
  const navigate = useNavigate();
  const name = useContext(CollectionNavigationName);
  const origin = titleOrigin(location, name);
  const navigationState = { ...state, titleReturn: origin };
  return <Link {...props} ref={ref} state={navigationState} onClick={event => {
    onClick?.(event);
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey || (props.target && props.target !== '_self') || props.reloadDocument) return;
    event.preventDefault();
    const isReturnPage = origin.href === `${location.pathname}${location.search}${location.hash}`;
    const titleReturn = isReturnPage ? { ...origin, scroll: captureReturnScroll() } : origin;
    void navigate(props.to, { state: { ...navigationState, titleReturn }, replace: props.replace, preventScrollReset: props.preventScrollReset });
  }} />;
});
