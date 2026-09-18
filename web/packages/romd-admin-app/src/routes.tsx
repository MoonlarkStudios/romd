import { Navigate, type RouteObject } from 'react-router';
import { ProtectedRoute } from './components/auth/ProtectedRoute';
import { AppLayout } from './components/layout/AppLayout';
import { Account } from './pages/Account';
import { ActivateAccount } from './pages/ActivateAccount';
import { Audit } from './pages/Audit';
import { AuthCallback } from './pages/AuthCallback';
import { Catalog } from './pages/Catalog/index';
import { CollectionWorkspace } from './pages/Collections/CollectionWorkspace';
import { Collections } from './pages/Collections/index';
import { Dashboard } from './pages/Dashboard/index';
import { Diagnostics } from './pages/Diagnostics/index';
import { Integrations } from './pages/Integrations';
import { Jobs } from './pages/Jobs/index';
import { JobDetail } from './pages/Jobs/JobDetail';
import { Libraries } from './pages/Libraries/index';
import { LibraryWorkspace } from './pages/Libraries/LibraryWorkspace';
import { Login } from './pages/Login';
import { LegacyRomRedirect, Roms } from './pages/Roms';
import { RomDetail } from './pages/Roms/RomDetail';
import { Search } from './pages/Search/index';
import { Settings } from './pages/Settings';
import { Storage } from './pages/Storage/index';
import { Systems } from './pages/Systems/index';
import { LegacyExplorerRedirect } from './pages/Systems/LegacyExplorerRedirect';
import { SourceWorkspace } from './pages/Systems/SourceWorkspace';
import { SystemHub } from './pages/Systems/SystemHub';
import { Taxonomy } from './pages/Taxonomy/index';
import { TitleDetail } from './pages/TitleDetail/index';
import { Users } from './pages/Users/index';
import { UserWorkspace } from './pages/Users/UserWorkspace';

export const routes: RouteObject[] = [
  { path: '/activate', element: <ActivateAccount /> },
  {
    path: '/login',
    element: <Login />,
  },
  {
    path: '/auth/callback',
    element: <AuthCallback />,
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
        element: <Dashboard />,
      },
      {
        path: 'import',
        element: (
          <ProtectedRoute requiredRoles={['Contributor', 'Manager', 'Admin']}>
            <LegacyRomRedirect importing />
          </ProtectedRoute>
        ),
      },
      {
        path: 'inbox',
        element: <LegacyRomRedirect />,
      },
      { path: 'roms', element: <Roms /> },
      { path: 'roms/:romId', element: <RomDetail /> },
      { path: 'roms/import', element: <ProtectedRoute requiredRoles={['Contributor', 'Manager', 'Admin']}><Roms importing /></ProtectedRoute> },
      {
        path: 'catalog',
        element: <Catalog />,
      },
      {
        path: 'collections',
        element: <Collections />,
      },
      {
        path: 'collections/:collectionId',
        element: <CollectionWorkspace />,
      },
      {
        path: 'titles/:titleId',
        element: <TitleDetail />,
      },
      {
        path: 'taxonomy',
        element: <Navigate to="/reference-data" replace />,
      },
      {
        path: 'search',
        element: <Search />,
      },
      {
        path: 'systems',
        element: <Systems />,
      },
      {
        path: 'systems/:systemKey',
        element: <SystemHub />,
      },
      { path: 'systems/:systemKey/sources/:sourceId', element: <SourceWorkspace /> },
      {
        path: 'registry/explorer',
        element: <LegacyExplorerRedirect />,
      },
      {
        path: 'jobs',
        element: <Jobs />,
      },
      { path: 'jobs/:jobId', element: <JobDetail /> },
      {
        path: 'libraries',
        element: <ProtectedRoute requiredRoles={['Admin']}><Libraries /></ProtectedRoute>,
      },
      {
        path: 'libraries/:libraryId',
        element: <ProtectedRoute requiredRoles={['Admin']}><LibraryWorkspace /></ProtectedRoute>,
      },
      {
        path: 'storage',
        element: <Storage />,
      },
      {
        path: 'users',
        element: (
          <ProtectedRoute requiredRoles={['Admin']}>
            <Users />
          </ProtectedRoute>
        ),
      },
      {
        path: 'diagnostics',
        element: (
          <ProtectedRoute requiredRoles={['Admin']}>
            <Diagnostics />
          </ProtectedRoute>
        ),
      },
      { path: 'reference-data', element: <ProtectedRoute requiredRoles={['Manager', 'Admin']}><Taxonomy /></ProtectedRoute> },
      { path: 'users/:userId', element: <ProtectedRoute requiredRoles={['Admin']}><UserWorkspace /></ProtectedRoute> },
      { path: 'audit', element: <ProtectedRoute requiredRoles={['Admin']}><Audit /></ProtectedRoute> },
      { path: 'account', element: <Account /> },
      { path: 'integrations', element: <ProtectedRoute requiredRoles={['Admin']}><Integrations /></ProtectedRoute> },
      { path: 'integrations/:providerId', element: <ProtectedRoute requiredRoles={['Admin']}><Integrations /></ProtectedRoute> },
      {
        path: 'settings',
        element: <ProtectedRoute requiredRoles={['Admin']}><Settings /></ProtectedRoute>,
      },
    ],
  },
];
