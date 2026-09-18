namespace Romd.Admin.Application.Libraries;

/// <summary>Worker materialization candidate port; shares the read-only contract with draft evaluation.</summary>
public interface IMaterializationDataProvider : ILibraryCandidateReader;
