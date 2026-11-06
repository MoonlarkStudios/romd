namespace Romd.Media.Processes;

/// <summary>Identity of the actual inherited admission file description, independent of its path.</summary>
internal sealed record MediaAdmissionIdentity(string Platform, ulong Device, ulong Inode);
