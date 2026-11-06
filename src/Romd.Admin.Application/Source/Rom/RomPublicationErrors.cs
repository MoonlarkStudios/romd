using ErrorOr;

namespace Romd.Admin.Application.Source.Rom;

public static class RomPublicationErrors
{
    public static Error AmbiguousProfile => Error.Validation("RomPublication.AmbiguousProfile", "The measured import has no unambiguous registered physical profile.");
    public const string RetryRequiredCode = "RomPublication.RetryRequired";
    public static Error RetryRequired() => Error.Conflict(RetryRequiredCode,
        "The retained representation changed before publication; retry with staged artifact bytes.");
}
