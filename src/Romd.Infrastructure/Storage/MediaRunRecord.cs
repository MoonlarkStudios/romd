using System.Text.Json;
using System.Text.Json.Serialization;
using Romd.Media.Processes;

namespace Romd.Infrastructure.Storage;

internal sealed record MediaRunRecord(string Version, string RunId, int Slot, int MaximumConcurrent,
    Guid? JobId, string Operation, string Phase, MediaProcessIdentity Parent, string? WorkerBuild, string? WindowsJob,
    MediaProcessIdentity? Identity, MediaAdmissionIdentity? Admission, IReadOnlyList<MediaStagingBinding> Staging)
{
    internal const string CurrentVersion = "romd-media-run/v2";
    internal const int MaximumBytes = 16384;
    internal const int MaximumCdBytes = 1 << 20;
    private static readonly JsonSerializerOptions _json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static MediaRunRecord? Read(string path, int slot, int maximumConcurrent)
    {
        if (!File.Exists(path))
            return null;
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumCdBytes)
            throw new InvalidDataException("Media run custody is oversized or empty.");
        byte[] bytes = BoundedRuntimeFile.Read(path, MaximumCdBytes);
        if (bytes.Length > MaximumCdBytes)
            throw new InvalidDataException("Media run custody is oversized.");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 5 });
            if (!Unique(document.RootElement) || !Fields(document.RootElement,
                ["Version", "RunId", "Slot", "MaximumConcurrent", "JobId", "Operation", "Phase", "Parent", "WorkerBuild", "WindowsJob", "Identity", "Admission", "Staging"]))
                throw new JsonException();
            var record = JsonSerializer.Deserialize<MediaRunRecord>(bytes, _json);
            if (record is null || bytes.Length > (CdOperation(record.Operation) ? MaximumCdBytes : MaximumBytes) || record.Version != CurrentVersion || record.Slot != slot || record.MaximumConcurrent != maximumConcurrent ||
                record.RunId is null || record.RunId.Length != 32 || !Guid.TryParseExact(record.RunId, "N", out _) || record.JobId == Guid.Empty ||
                !Label(record.Operation, 64) || record.Phase is not ("prepared" or "launching" or "running" or "permitted" or "stopped") ||
                record.Staging is null || record.Staging.Count > (CdOperation(record.Operation) ? 128 : 3) || record.Staging.Any(binding =>
                    binding is null || binding.Path is null || binding.PoolRoot is null ||
                    !Path.IsPathFullyQualified(binding.Path) || binding.Path.Length > 2048 || binding.ReservedBytes is < 0 or > (16L << 40) ||
                    !Path.IsPathFullyQualified(binding.PoolRoot) || binding.PoolRoot.Length > 2048 || binding.Slot is < -1 or > 255 || binding.JobId != record.JobId ||
                    binding.Role is not ("source" or "output" or "verification")) ||
                !ValidIdentity(record.Parent) || record.Identity is { } identity && !ValidIdentity(identity) ||
                !Fields(document.RootElement.GetProperty("Parent"), ["ProcessId", "GroupId", "Platform", "Boot", "Birth", "PidNamespace"]) ||
                document.RootElement.GetProperty("Identity") is { ValueKind: not JsonValueKind.Null } measured &&
                    !Fields(measured, ["ProcessId", "GroupId", "Platform", "Boot", "Birth", "PidNamespace"]) ||
                document.RootElement.GetProperty("Staging").EnumerateArray().Any(binding =>
                    !Fields(binding, ["PoolRoot", "Slot", "Path", "ReservedBytes", "JobId", "AttemptOperation", "Role"])) ||
                (record.Parent.Platform == "windows" ? record.Admission is not null : record.Admission is null ||
                    record.Admission.Platform != record.Parent.Platform || record.Admission.Inode == 0 ||
                    !Fields(document.RootElement.GetProperty("Admission"), ["Platform", "Device", "Inode"])))
                throw new JsonException();
            return record;
        }
        catch (JsonException error) { throw new InvalidDataException("Media run custody is unrecognized.", error); }
    }

    internal static void Write(string path, MediaRunRecord record)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, _json);
        if (bytes.Length > (CdOperation(record.Operation) ? MaximumCdBytes : MaximumBytes))
            throw new InvalidDataException("Media run custody exceeds its bounded protocol.");
        string next = path + ".new";
        using (var file = new FileStream(next, FileMode.Create, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(flushToDisk: true); }
        File.Move(next, path, overwrite: true);
    }

    internal static bool Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
                if (!fields.Add(field.Name) || !Unique(field.Value))
                    return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray())
                if (!Unique(element))
                    return false;
        return true;
    }
    private static bool CdOperation(string? operation) => operation is "encode-chd-cd" or "verify-chd-cd" or "reverify-chd-cd" or "media-verification-cd";
    private static bool Label(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl);
    private static bool Fields(JsonElement value, string[] names) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Count() == names.Length && value.EnumerateObject().All(field => names.Contains(field.Name, StringComparer.Ordinal));
    private static bool ValidIdentity(MediaProcessIdentity? value) => value is not null && value.ProcessId > 0 && value.GroupId >= 0 &&
        value.Platform is "linux" or "darwin" or "windows" && Label(value.Boot, 128) && Label(value.Birth, 128) &&
        MediaProcessIdentity.ValidNamespace(value.Platform, value.PidNamespace);
}
