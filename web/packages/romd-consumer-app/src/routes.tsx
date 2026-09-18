import type { RouteObject } from 'react-router';
import { ProtectedRoute } from './components/auth/ProtectedRoute';
import { AppLayout } from './components/layout/AppLayout';
import { Activity } from './pages/Activity';
import { ActivitySession } from './pages/ActivitySession';
import { AuthCallback } from './pages/AuthCallback';
import { BrowserPlayer } from './pages/BrowserPlayer';
import { CollectionDetail } from './pages/CollectionDetail';
import { Library } from './pages/Library';
import { Login } from './pages/Login';
import { NotFound } from './pages/NotFound';
import { Shelves } from './pages/Shelves';
import { TitleDetail } from './pages/TitleDetail';

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
