namespace Romd.Hosting.IntegrationTests.RealtimeGuardFixtures;

public sealed record RawIdentityGuardMessage(
    int LibraryId,
    IReadOnlyList<int> LibraryIds,
    RawIdentityGuardNestedMessage Nested,
    int ItemCount);

public sealed record RawIdentityGuardNestedMessage(IReadOnlyList<int> TitleIds);
