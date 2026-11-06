import { lazy } from 'react';
import type { RouteObject } from 'react-router';
import { ProtectedRoute } from './components/auth/ProtectedRoute';
import { AppLayout } from './components/layout/AppLayout';

const Activity = lazy(() =>
  import('./pages/Activity').then((module) => ({
    default: module.Activity,
  })),
);
const ActivitySession = lazy(() =>
  import('./pages/ActivitySession').then((module) => ({
    default: module.ActivitySession,
  })),
);
const AuthCallback = lazy(() =>
  import('./pages/AuthCallback').then((module) => ({
    default: module.AuthCallback,
  })),
);
const BrowserPlayer = lazy(() =>
  import('./pages/BrowserPlayer').then((module) => ({
    default: module.BrowserPlayer,
  })),
);
const CollectionDetail = lazy(() =>
  import('./pages/CollectionDetail').then((module) => ({
    default: module.CollectionDetail,
  })),
);
const Connections = lazy(() =>
  import('./pages/Connections').then((module) => ({
    default: module.Connections,
  })),
);
const Library = lazy(() =>
  import('./pages/Library').then((module) => ({
    default: module.Library,
  })),
);
const Login = lazy(() =>
  import('./pages/Login').then((module) => ({
    default: module.Login,
  })),
);
const NotFound = lazy(() =>
  import('./pages/NotFound').then((module) => ({
    default: module.NotFound,
  })),
);
const Shelves = lazy(() =>
  import('./pages/Shelves').then((module) => ({
    default: module.Shelves,
  })),
);
const TitleDetail = lazy(() =>
  import('./pages/TitleDetail').then((module) => ({
    default: module.TitleDetail,
  })),
);

export const routes: RouteObject[] = [
  {
    path: '/login',
    element: <Login />,
  },
  {
    path: '/auth/callback',
    element: <AuthCallback />,
  },
  {
    path: '/titles/:titleId/releases/:releaseId/play',
    element: (
      <ProtectedRoute>
        <BrowserPlayer />
      </ProtectedRoute>
    ),
  },
  {
    path: '/',
    element: (
      <ProtectedRoute>
        <AppLayout />
      </ProtectedRoute>
    ),
    children: [
      {
        index: true,
        element: <Shelves />,
      },
      {
        path: 'library',
        element: <Library />,
      },
      {
        path: 'connections',
        element: <Connections />,
      },
      {
        path: 'activity',
        element: <Activity />,
      },
      {
        path: 'activity/sessions/:sessionId',
        element: <ActivitySession />,
      },
      {
        path: 'titles/:titleId',
        element: <TitleDetail />,
      },
      {
        path: 'collections/:collectionId',
        element: <CollectionDetail />,
      },
      {
        path: '*',
        element: <NotFound />,
      },
    ],
  },
];
