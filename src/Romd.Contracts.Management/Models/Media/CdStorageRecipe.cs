namespace Romd.Contracts.Management.Models.Media;

public sealed record CdStorageRecipe(bool Available, string RecipeVersion, int HunkBytes, IReadOnlyList<string> Codecs, string QualifiedRid);
