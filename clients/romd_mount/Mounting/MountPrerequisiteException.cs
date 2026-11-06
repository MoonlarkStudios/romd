namespace Romd.Mount.Mounting;

// Messages are fixed local guidance, never child-process or remote response text.
public sealed class MountPrerequisiteException(string message) : Exception(message);
