namespace Romd.Contracts.Management.Models;

/// <summary>Current storage, catalog coverage, and host health for the admin status bar.</summary>
public sealed record DashboardStatusBar(StorageStats Storage, CoverageStats Coverage, SystemHealth Health);
