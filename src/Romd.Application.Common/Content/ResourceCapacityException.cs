namespace Romd.Application.Common.Content;

/// <summary>
/// Temporary admission pressure. The caller may release its resources and retry later.
/// Invalid inputs, execution deadlines and configuration failures must not use this signal.
/// </summary>
public sealed class ResourceCapacityException(string message) : Exception(message);
