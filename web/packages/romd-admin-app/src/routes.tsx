import { lazy } from 'react';
import { Navigate, type RouteObject } from 'react-router';
import { ProtectedRoute } from './components/auth/ProtectedRoute';
import { AppLayout } from './components/layout/AppLayout';

const Account = lazy(() =>
  import('./pages/Account').then((module) => ({
    default: module.Account,
  })),
);
const ActivateAccount = lazy(() =>
  import('./pages/ActivateAccount').then((module) => ({
    default: module.ActivateAccount,
  })),
);
const Audit = lazy(() =>
  import('./pages/Audit').then((module) => ({
    default: module.Audit,
  })),
);
const AuthCallback = lazy(() =>
  import('./pages/AuthCallback').then((module) => ({
    default: module.AuthCallback,
  })),
);
const Catalog = lazy(() =>
  import('./pages/Catalog/index').then((module) => ({
    default: module.Catalog,
  })),
);
const CollectionWorkspace = lazy(() =>
  import('./pages/Collections/CollectionWorkspace').then((module) => ({
    default: module.CollectionWorkspace,
  })),
);
const Collections = lazy(() =>
  import('./pages/Collections/index').then((module) => ({
    default: module.Collections,
  })),
);
const Dashboard = lazy(() =>
  import('./pages/Dashboard/index').then((module) => ({
    default: module.Dashboard,
  })),
);
const DiscLayouts = lazy(() =>
  import('./pages/DiscLayouts').then((module) => ({
    default: module.DiscLayouts,
  })),
);
const Diagnostics = lazy(() =>
  import('./pages/Diagnostics/index').then((module) => ({
    default: module.Diagnostics,
  })),
);
const EnrichmentWorkspace = lazy(() =>
  import('./pages/Enrichment/EnrichmentWorkspace').then((module) => ({
    default: module.EnrichmentWorkspace,
  })),
);
const Integrations = lazy(() =>
  import('./pages/Integrations').then((module) => ({
    default: module.Integrations,
  })),
);
const Jobs = lazy(() =>
  import('./pages/Jobs/index').then((module) => ({
    default: module.Jobs,
  })),
);
const JobDetail = lazy(() =>
  import('./pages/Jobs/JobDetail').then((module) => ({
    default: module.JobDetail,
  })),
);
const Libraries = lazy(() =>
  import('./pages/Libraries/index').then((module) => ({
    default: module.Libraries,
  })),
);
const LibraryWorkspace = lazy(() =>
  import('./pages/Libraries/LibraryWorkspace').then((module) => ({
    default: module.LibraryWorkspace,
  })),
);
const Login = lazy(() =>
  import('./pages/Login').then((module) => ({
    default: module.Login,
  })),
);
const LegacyRomRedirect = lazy(() =>
  import('./pages/Roms').then((module) => ({
    default: module.LegacyRomRedirect,
  })),
);
const Roms = lazy(() =>
  import('./pages/Roms').then((module) => ({
    default: module.Roms,
  })),
);
const RomDetail = lazy(() =>
  import('./pages/Roms/RomDetail').then((module) => ({
    default: module.RomDetail,
  })),
);
const Search = lazy(() =>
  import('./pages/Search/index').then((module) => ({
    default: module.Search,
  })),
);
const Settings = lazy(() =>
  import('./pages/Settings').then((module) => ({
    default: module.Settings,
  })),
);
const Storage = lazy(() =>
  import('./pages/Storage/index').then((module) => ({
    default: module.Storage,
  })),
);
const Systems = lazy(() =>
  import('./pages/Systems/index').then((module) => ({
    default: module.Systems,
  })),
);
const LegacyExplorerRedirect = lazy(() =>
  import('./pages/Systems/LegacyExplorerRedirect').then((module) => ({
    default: module.LegacyExplorerRedirect,
  })),
);
const SourceWorkspace = lazy(() =>
  import('./pages/Systems/SourceWorkspace').then((module) => ({
    default: module.SourceWorkspace,
  })),
);
const SystemHub = lazy(() =>
  import('./pages/Systems/SystemHub').then((module) => ({
    default: module.SystemHub,
  })),
);
const Taxonomy = lazy(() =>
  import('./pages/Taxonomy/index').then((module) => ({
    default: module.Taxonomy,
  })),
);
const TitleDetail = lazy(() =>
  import('./pages/TitleDetail/index').then((module) => ({
    default: module.TitleDetail,
  })),
);
const Users = lazy(() =>
  import('./pages/Users/index').then((module) => ({
    default: module.Users,
  })),
);
const UserWorkspace = lazy(() =>
  import('./pages/Users/UserWorkspace').then((module) => ({
    default: module.UserWorkspace,
  })),
);

export const routes: RouteObject[] = [
  {
    path: '/activate',
    element: <ActivateAccount />,
  },
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
          <ProtectedRoute
            requiredRoles={[
              'Contributor',
              'Manager',
              'Admin',
            ]}
          >
            <LegacyRomRedirect importing />
          </ProtectedRoute>
        ),
      },
      {
        path: 'inbox',
        element: <LegacyRomRedirect />,
      },
      {
        path: 'disc-layouts',
        element: (
          <ProtectedRoute
            requiredRoles={[
              'Admin',
            ]}
          >
            <DiscLayouts />
          </ProtectedRoute>
        ),
      },
      {
        path: 'roms',
        element: <Roms />,
      },
      {
        path: 'roms/:romId',
        element: <RomDetail />,
      },
      {
        path: 'roms/import',
        element: (
          <ProtectedRoute
            requiredRoles={[
              'Contributor',
              'Manager',
              'Admin',
            ]}
          >
            <Roms importing />
          </ProtectedRoute>
        ),
      },
      {
        path: 'catalog',
        element: <Catalog />,
      },
      {
        path: 'enrichment',
        element: (
          <ProtectedRoute
            requiredRoles={[
              'Manager',
              'Admin',
            ]}
          >
            <EnrichmentWorkspace />
          </ProtectedRoute>
        ),
      },
      {
        path: 'enrichment/titles/:titleId',
        element: (
          <ProtectedRoute
            requiredRoles={[
              'Manager',
              'Admin',
            ]}
          >
            <EnrichmentWorkspace />
          </ProtectedRoute>
        ),
      },
      {
        path: 'enrichment/:taskId',
        element: (
          <ProtectedRoute
            requiredRoles={[
              'Manager',
              'Admin',
            ]}
          >
            <EnrichmentWorkspace />
          </ProtectedRoute>
        ),
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
        element: (
          <Navigate
            to="/reference-data"
            replace
          />
        ),
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
      {
        path: 'systems/:systemKey/sources/:sourceId',
        element: <SourceWorkspace />,
      },
      {
        path: 'registry/explorer',
        element: <LegacyExplorerRedirect />,
      },
      {
        path: 'jobs',
        element: <Jobs />,
      },
      {
        path: 'jobs/:jobId',
        element: <JobDetail />,
      },
      {
        path: 'libraries',
        element: (
          <ProtectedRoute
            requiredRoles={[
              'Admin',
            ]}
          >
            <Libraries />
          </ProtectedRoute>
        ),
      },
      {
        path: 'libraries/:libraryId',
        element: (
          <ProtectedRoute
            requiredRoles={[
              'Admin',
            ]}
          >
            <LibraryWorkspace />
          </ProtectedRoute>
        ),
      },
      {
        path: 'storage',
        element: <Storage />,
      },
      {
        path: 'users',
        element: (
          <ProtectedRoute
            requiredRoles={[
              'Admin',
            ]}
          >
            <Users />
          </ProtectedRoute>
        ),
      },
      {
        path: 'diagnostics',
        element: (
          <ProtectedRoute
            requiredRoles={[
              'Admin',
            ]}
          >
            <Diagnostics />
          </ProtectedRoute>
        ),
      },
      {
        path: 'reference-data',
        element: (
          <ProtectedRoute
            requiredRoles={[
              'Manager',
              'Admin',
            ]}
          >
            <Taxonomy />
          </ProtectedRoute>
        ),
      },
      {
        path: 'users/:userId',
        element: (
          <ProtectedRoute
            requiredRoles={[
              'Admin',
            ]}
          >
            <UserWorkspace />
          </ProtectedRoute>
        ),
      },
      {
        path: 'audit',
        element: (
          <ProtectedRoute
            requiredRoles={[
              'Admin',
            ]}
          >
            <Audit />
          </ProtectedRoute>
        ),
      },
      {
        path: 'account',
        element: <Account />,
      },
      {
        path: 'integrations',
        element: (
          <ProtectedRoute
            requiredRoles={[
              'Admin',
            ]}
          >
            <Integrations />
          </ProtectedRoute>
        ),
      },
      {
        path: 'integrations/:providerId',
        element: (
          <ProtectedRoute
            requiredRoles={[
              'Admin',
            ]}
          >
            <Integrations />
          </ProtectedRoute>
        ),
      },
      {
        path: 'settings',
        element: (
          <ProtectedRoute
            requiredRoles={[
              'Admin',
            ]}
          >
            <Settings />
          </ProtectedRoute>
        ),
      },
    ],
  },
];
