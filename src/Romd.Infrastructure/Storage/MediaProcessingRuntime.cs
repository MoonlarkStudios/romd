using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ErrorOr;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Media.Processes;

namespace Romd.Infrastructure.Storage;

/// <summary>Shared admission and durable child custody, independent of a worker process lifetime.</summary>
public sealed class MediaProcessingRuntime : IMediaRunCustody
{
    private static readonly AsyncLocal<MediaProcessingReservation?> Ambient = new();
    private static readonly ConcurrentDictionary<string, MediaProcessingReservation> Retained = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, MediaProcessingReservation> Owned = new(StringComparer.Ordinal);
    private readonly MediaProcessingOptions _limits;
    private readonly string _root;
    private readonly Func<int, MediaProcessObservation> _observeIdentity;
    private readonly Func<MediaProcessIdentity?> _readSelfIdentity;
    private readonly Func<MediaProcessIdentity?> _readNamespaceInitIdentity;
    internal static MediaProcessingReservation? Current => Ambient.Value;

    public MediaProcessingRuntime(IRomdOptions options, MediaProcessingOptions limits)
        : this(Path.GetFullPath(Path.Combine(options.DataDirectory, "temp", "media-runs-v1")), limits) { }
    internal MediaProcessingRuntime(string root, MediaProcessingOptions limits, Func<int, MediaProcessIdentity?>? readIdentity = null,
        Func<MediaProcessIdentity?>? readSelfIdentity = null, Func<MediaProcessIdentity?>? readNamespaceInitIdentity = null)
    {
        _root = Path.GetFullPath(root);
        _limits = limits.ValidatedCopy();
        _observeIdentity = readIdentity is null ? MediaProcessIdentity.Observe : pid =>
            readIdentity(pid) is { } identity ? new(MediaProcessPresence.Known, identity) : new(MediaProcessPresence.Unknown);
        _readSelfIdentity = readSelfIdentity ?? (() => MediaProcessIdentity.Read(Environment.ProcessId));
        _readNamespaceInitIdentity = readNamespaceInitIdentity ?? MediaProcessIdentity.ReadNamespaceInit;
        Directory.CreateDirectory(_root);
    }
    internal static MediaProcessingRuntime TestLocal(MediaProcessingOptions limits) =>
        new(Directory.CreateTempSubdirectory("romd-test-media-runtime-").FullName, limits);

    public async Task<MediaProcessingReservation> AcquireAsync(Guid? jobId = null, string operation = "preparation", CancellationToken ct = default)
    {
        if (jobId == Guid.Empty || string.IsNullOrWhiteSpace(operation) || operation.Length > 64 || operation.Any(char.IsControl))
            throw new ArgumentException("Media processing reservation identity is invalid.");
        long started = Stopwatch.GetTimestamp();
        do
        {
            ct.ThrowIfCancellationRequested();
            using var configuration = TrySharedLock("accounting.lease");
            if (configuration is null)
            { await Task.Delay(25, ct); continue; }
            ValidateConfiguration();
            for (int slot = 0; slot < _limits.MaxConcurrent; slot++)
            {
                IDisposable? owner = TryLock(slot);
                if (owner is null)
                {
                    await TryStopOrphanAsync(slot, ct);
                    continue;
                }
                try
                {
                    string path = RecordPath(slot);
                    MediaRunRecord? previous;
                    try
                    { previous = MediaRunRecord.Read(path, slot, _limits.MaxConcurrent); }
                    catch (InvalidDataException) { continue; } // Unknown custody never becomes available capacity.
                    if (previous is not null && !await RecoverAsync(previous, path, owner, ct))
                        continue;
                    string nonce = Guid.NewGuid().ToString("N");
                    if (owner is NativeFileLease native)
                        native.Bind(nonce);
                    var record = new MediaRunRecord(MediaRunRecord.CurrentVersion, nonce, slot, _limits.MaxConcurrent,
                        jobId, operation, "prepared", MediaProcessIdentity.Read(Environment.ProcessId)
                            ?? throw new IOException("The parent process identity cannot be established."), null, null, null,
                        owner is NativeFileLease held ? held.Identity : null, []);
                    MediaRunRecord.Write(path, record);
                    var result = new MediaProcessingReservation(this, owner, path, record);
                    Owned[path] = result;
                    owner = null;
                    return result;
                }
                finally { owner?.Dispose(); }
            }
            // Back off without accounting: an owner confirming custody before release must not be starved.
            configuration.Dispose();
            await Task.Delay(25, ct);
        } while (Stopwatch.GetElapsedTime(started).TotalSeconds < _limits.AdmissionTimeoutSeconds);
        throw new ResourceCapacityException("Media processing capacity or unresolved child custody is occupied; retry later.");
    }

    public async Task<ErrorOr<Success>> EnsureJobStoppedAsync(Guid jobId, CancellationToken ct = default)
    {
        if (jobId == Guid.Empty)
            return Error.Validation("Rom.Media.Custody", "A durable job identity is required.");
        using var configuration = await WaitForAccountingLockAsync(ct);
        if (configuration is null)
            return Error.Validation("Rom.Media.Custody", "Media custody accounting is occupied.");
        try
        { ValidateConfiguration(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        { return Error.Validation("Rom.Media.Custody", "Shared media custody configuration is unrecognized."); }
        for (int slot = 0; slot < _limits.MaxConcurrent; slot++)
        {
            ct.ThrowIfCancellationRequested();
            string path = RecordPath(slot);
            MediaRunRecord? record;
            try
            { record = MediaRunRecord.Read(path, slot, _limits.MaxConcurrent); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            { return Error.Validation("Rom.Media.Custody", "Media run custody is unrecognized or unreadable."); }
            if (record is null || record.JobId != jobId)
                continue;
            if (Owned.TryGetValue(path, out var current) && current.ConfirmsStopped(record))
                continue;
            await TryStopOrphanAsync(slot, ct);
            using var owner = TryLock(slot);
            if (owner is null || !await RecoverAsync(record, path, owner, ct))
                return Error.Validation("Rom.Media.Custody", "The job's media process trees have not been confirmed stopped.");
        }
        return Result.Success;
    }

    private async Task<IDisposable?> WaitForAccountingLockAsync(CancellationToken ct)
    {
        // Another admission or recovery may briefly hold accounting. Wait within the
        // existing admission budget before treating that contention as unavailable custody.
        long started = Stopwatch.GetTimestamp();
        do
        {
            ct.ThrowIfCancellationRequested();
            var lease = TrySharedLock("accounting.lease");
            if (lease is not null)
                return lease;
            await Task.Delay(25, ct);
        } while (Stopwatch.GetElapsedTime(started).TotalSeconds < _limits.AdmissionTimeoutSeconds);
        return null;
    }

    private async Task<bool> RecoverAsync(MediaRunRecord record, string path, IDisposable owner, CancellationToken ct)
    {
        try
        { return await RecoverCoreAsync(record, path, owner, ct); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or Win32Exception)
        { return false; }
    }

    private async Task<bool> RecoverCoreAsync(MediaRunRecord record, string path, IDisposable owner, CancellationToken ct)
    {
        // This method is called only with a newly acquired independent admission description.
        // A child which inherited the old description cannot still exist with that fd open.
        if (owner is NativeFileLease held && held.Identity != record.Admission)
            return false;
        var identity = record.Identity ?? ReadIdentity(path, record.RunId, record.Admission);
        if (identity is not null && record.Phase != "stopped")
        {
            var here = _readSelfIdentity();
            if (here is null || identity.Platform != here.Platform || identity.Boot != here.Boot)
                return false;
            if (record.Parent.Platform != identity.Platform || record.Parent.Boot != identity.Boot ||
                record.Parent.PidNamespace != identity.PidNamespace)
                return false;
            bool foreign = identity.PidNamespace != here.PidNamespace;
            if (identity.Platform == "linux" && record.Parent.ProcessId == 1)
            {
                // Namespace inode labels can be recycled. A namespace init cannot
                // be replaced within its incarnation; ambiguous init facts stay pinned.
                if (!DifferentNamespaceIncarnation(record.Parent, here))
                    return false;
                foreign = true;
            }
            if (foreign)
            {
                // Approved Linux namespace-init extinction rule, rather than a
                // local PID query or an OS reap claim. The original PID1 kept
                // this exact OFD rooted through every unconfirmed child use.
                if (owner is not NativeFileLease native || !ForeignNamespaceExtinct(record, identity, here, native.Identity))
                    return false;
            }
            else
            {
                if (!CanObserveLocalRun(record.Parent, identity, here))
                    return false;
                var observed = _observeIdentity(identity.ProcessId);
                if (observed.Presence != MediaProcessPresence.Gone &&
                    (observed.Presence != MediaProcessPresence.Known || observed.Identity != identity ||
                        !MediaWorkerProcessLifetime.IsAdoptedZombie(identity)))
                    return false;
            }
            if (!foreign && identity.Platform == "windows")
            {
                if (!MediaWorkerProcessLifetime.IsWindowsJobStopped(record.WindowsJob))
                    return false;
            }
            else if (!foreign)
            {
                if (identity.GroupId != identity.ProcessId)
                    return false;
                // A restarted Linux parent may adopt descendants. Reap only this recognized group.
                using var lifetime = new MediaWorkerProcessLifetime();
                try
                { await lifetime.JoinDescendantsAsync(identity.GroupId, ct); }
                catch (MediaWorkerCleanupException) { return false; }
            }
        }
        else if (identity is null && record.Phase is not ("prepared" or "stopped"))
            return false;
        // A prepared record is an affirmative prelaunch certificate: the durable
        // launching transition precedes every native spawn. Unidentified launches stay pinned.
        try
        {
            foreach (var binding in record.Staging)
            {
                if (binding.Slot < 0)
                    return false; // Non-pool/legacy custody requires its explicit owner.
                if (!await TempFilePool.RecoverChildHoldAsync(binding, record.RunId, path, ct))
                    return false;
            }
            File.Delete(path + ".identity");
            File.Delete(path);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    internal static bool ForeignNamespaceExtinct(MediaRunRecord record, MediaProcessIdentity child, MediaProcessIdentity here, MediaAdmissionIdentity acquired) =>
        record.Version == MediaRunRecord.CurrentVersion && record.Phase is "launching" or "running" or "permitted" &&
        here.Platform == "linux" && child.Platform == "linux" && record.Parent.Platform == "linux" &&
        record.Parent.ProcessId == 1 && child.GroupId == child.ProcessId && record.Parent.Boot == child.Boot && child.Boot == here.Boot &&
        record.Parent.PidNamespace == child.PidNamespace && DifferentNamespaceIncarnation(record.Parent, here) &&
        MediaProcessIdentity.ValidNamespace("linux", child.PidNamespace) && MediaProcessIdentity.ValidNamespace("linux", here.PidNamespace) &&
        acquired.Platform == "linux" && acquired.Inode > 0 && acquired == record.Admission;

    internal static bool DifferentNamespaceIncarnation(MediaProcessIdentity parent, MediaProcessIdentity here) =>
        parent.Platform == "linux" && here.Platform == "linux" && parent.Boot == here.Boot && parent.ProcessId == 1 &&
        MediaProcessIdentity.ValidNamespace("linux", parent.PidNamespace) && MediaProcessIdentity.ValidNamespace("linux", here.PidNamespace) &&
        (parent.PidNamespace != here.PidNamespace || here.ProcessId == 1 &&
            ulong.TryParse(parent.Birth, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong oldBirth) && oldBirth > 0 &&
            ulong.TryParse(here.Birth, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong currentBirth) && currentBirth > 0 &&
            oldBirth != currentBirth);

    private bool CanObserveLocalRun(MediaProcessIdentity parent, MediaProcessIdentity child, MediaProcessIdentity here) =>
        here.Platform != "linux" || HasLocalLinuxChronology(parent, child, here,
            here.ProcessId == 1 ? here : _readNamespaceInitIdentity());

    internal static bool HasLocalLinuxChronology(MediaProcessIdentity parent, MediaProcessIdentity child,
        MediaProcessIdentity here, MediaProcessIdentity? init) =>
        init is { Platform: "linux", ProcessId: 1 } && here.Platform == "linux" && parent.Platform == "linux" && child.Platform == "linux" &&
        parent.ProcessId > 1 && child.ProcessId > 1 && !string.IsNullOrWhiteSpace(here.Boot) &&
        init.Boot == here.Boot && parent.Boot == here.Boot && child.Boot == here.Boot &&
        MediaProcessIdentity.ValidNamespace("linux", here.PidNamespace) && init.PidNamespace == here.PidNamespace &&
        parent.PidNamespace == here.PidNamespace && child.PidNamespace == here.PidNamespace &&
        ulong.TryParse(init.Birth, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong initBirth) &&
        ulong.TryParse(parent.Birth, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong parentBirth) &&
        ulong.TryParse(child.Birth, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong childBirth) &&
        parentBirth > initBirth && childBirth > initBirth;

    internal static MediaProcessIdentity? ReadIdentity(string path, string runId, MediaAdmissionIdentity? expectedAdmission = null)
    {
        string identityPath = path + ".identity";
        if (!File.Exists(identityPath))
            return null;
        if (new FileInfo(identityPath).Length is <= 0 or > 2048)
            throw new InvalidDataException("Media child identity is oversized.");
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(BoundedRuntimeFile.Read(identityPath, 2048));
            var root = json.RootElement;
            if (!MediaRunRecord.Unique(root) || root.ValueKind != System.Text.Json.JsonValueKind.Object || root.EnumerateObject().Count() != 9 ||
                root.GetProperty("Version").GetString() != "romd-media-child/v2" || root.GetProperty("RunId").GetString() != runId)
                throw new InvalidDataException("Media child identity is unrecognized.");
            var identity = new MediaProcessIdentity(root.GetProperty("ProcessId").GetInt32(), root.GetProperty("GroupId").GetInt32(),
                root.GetProperty("Platform").GetString()!, root.GetProperty("Boot").GetString()!, root.GetProperty("Birth").GetString()!, root.GetProperty("PidNamespace").GetString()!);
            if (identity.ProcessId <= 0 || identity.GroupId < 0 || identity.Platform is not ("linux" or "darwin" or "windows") ||
                identity.Boot is not { Length: >= 1 and <= 128 } || identity.Birth is not { Length: >= 1 and <= 128 } ||
                !MediaProcessIdentity.ValidNamespace(identity.Platform, identity.PidNamespace))
                throw new InvalidDataException("Media child identity is invalid.");
            var admission = root.GetProperty("Admission");
            if (identity.Platform == "windows" ? admission.ValueKind != System.Text.Json.JsonValueKind.Null :
                expectedAdmission is null || admission.ValueKind != System.Text.Json.JsonValueKind.Object || admission.EnumerateObject().Count() != 3 ||
                admission.GetProperty("Platform").GetString() != expectedAdmission.Platform ||
                admission.GetProperty("Device").GetUInt64() != expectedAdmission.Device || admission.GetProperty("Inode").GetUInt64() != expectedAdmission.Inode)
                throw new InvalidDataException("Media child admission identity differs from its run.");
            return identity;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("Media child identity is unrecognized.", error); }
    }

    private IDisposable? TryLock(int slot)
        => TrySharedLock($"processing-{slot}.lease");
    private IDisposable? TrySharedLock(string name)
    {
        string path = Path.Combine(_root, name);
        if (!OperatingSystem.IsWindows())
            return NativeFileLease.TryAcquire(path);
        try
        { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return null; }
    }
    private string RecordPath(int slot) => Path.Combine(_root, $"run-{slot}.json");

    private void ValidateConfiguration()
    {
        string configuration = Path.Combine(_root, "capacity.json");
        if (File.Exists(configuration))
        {
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(BoundedRuntimeFile.Read(configuration, 1024));
                var root = json.RootElement;
                if (!MediaRunRecord.Unique(root) || root.EnumerateObject().Count() != 2 ||
                    root.GetProperty("Version").GetString() != "romd-media-capacity/v1")
                    throw new InvalidDataException("Unrecognized shared media capacity.");
                int maximum = root.GetProperty("MaximumConcurrent").GetInt32();
                if (maximum is < 1 or > 16)
                    throw new InvalidDataException("Invalid shared media capacity.");
                if (maximum != _limits.MaxConcurrent)
                {
                    if (Directory.EnumerateFiles(_root, "run-*.json").Any())
                        throw new InvalidDataException("Processing limits differ across live or unresolved hosts.");
                    WriteCapacity();
                }
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            { throw new InvalidDataException("Unrecognized shared media capacity.", error); }
        }
        else
        {
            if (Directory.EnumerateFiles(_root, "run-*.json").Any())
                throw new InvalidDataException("Media capacity is missing while run custody exists.");
            WriteCapacity();
        }
        // An older host's slots cannot disappear from accounting when a new limit is smaller.
        foreach (string path in Directory.EnumerateFiles(_root, "run-*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (!int.TryParse(name.AsSpan(4), out int slot) || slot < 0 || slot >= _limits.MaxConcurrent)
                throw new InvalidDataException("Media run slots differ from the configured shared processing capacity.");
        }
        void WriteCapacity()
        {
            byte[] bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            { Version = "romd-media-capacity/v1", MaximumConcurrent = _limits.MaxConcurrent });
            using (var next = new FileStream(configuration + ".new", FileMode.Create, FileAccess.Write, FileShare.None))
            { next.Write(bytes); next.Flush(flushToDisk: true); }
            File.Move(configuration + ".new", configuration, overwrite: true);
        }
    }

    private async Task TryStopOrphanAsync(int slot, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return; // Kill-on-close job enforces orphan termination on this platform.
        using var guard = NativeFileLease.TryAcquire(Path.Combine(_root, "recovery.lease"));
        if (guard is null)
            return;
        string path = RecordPath(slot);
        MediaRunRecord? record;
        MediaProcessIdentity? child;
        try
        {
            record = MediaRunRecord.Read(path, slot, _limits.MaxConcurrent);
            if (record is null || record.Phase == "stopped")
                return;
            var here = _readSelfIdentity();
            if (here is null || record.Parent.Platform != here.Platform || record.Parent.Boot != here.Boot || record.Parent.PidNamespace != here.PidNamespace)
                return;
            // Init death extinguishes its namespace; it cannot leave a local
            // orphan. Reused or ambiguous init labels require independent OFD recovery.
            if (here.Platform == "linux" && record.Parent.ProcessId == 1)
                return;
            child = record.Identity ?? ReadIdentity(path, record.RunId, record.Admission);
            if (child is null || child.Platform != here.Platform || child.Boot != here.Boot || child.PidNamespace != here.PidNamespace ||
                child.GroupId != child.ProcessId || !CanObserveLocalRun(record.Parent, child, here))
                return;
            var parent = _observeIdentity(record.Parent.ProcessId);
            if (parent.Presence == MediaProcessPresence.Unknown || parent.Identity == record.Parent)
                return;
            if (parent.Identity is { } observed && (observed.Platform != here.Platform || observed.Boot != here.Boot || observed.PidNamespace != here.PidNamespace))
                return;
            if (_observeIdentity(child.ProcessId).Identity != child)
                return;
            // A matched birth and session plus the old inherited admission lock establish custody.
            // Never target an identity which has been reused or cannot be independently read.
            using var independent = TryLock(slot);
            if (independent is not null)
                return;
            using var lifetime = new MediaWorkerProcessLifetime();
            lifetime.Terminate(child.ProcessId, leaderExited: false);
            await lifetime.JoinDescendantsAsync(child.GroupId, ct);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or
            KeyNotFoundException or InvalidOperationException or Win32Exception)
        { /* Uncertain orphan custody remains charged. */ }
    }

    public sealed class MediaProcessingReservation : IDisposable
    {
        private readonly IDisposable _owner;
        private MediaRunRecord _record;
        private int _disposed;
        private bool _retained;
        private bool _childActive;
        private bool _stopProof = true; // An acquired, unstarted reservation has no child.
        private IReadOnlyList<MediaStagingBinding> _aggregate = [];
        internal string RecordPath { get; }
        internal string Nonce => _record.RunId;
        internal NativeFileLease? NativeLease => _owner as NativeFileLease;
        public Guid? JobId => _record.JobId;
        internal bool ProcessesStopped => _stopProof && !_childActive && !_retained;
        internal bool Released => _disposed != 0 && !_retained;
        internal MediaProcessingReservation(MediaProcessingRuntime runtime, IDisposable owner, string path, MediaRunRecord record)
        { _owner = owner; RecordPath = path; _record = record; }
        internal IDisposable Activate()
        {
            var previous = Ambient.Value;
            Ambient.Value = this;
            return new Activation(previous);
        }
        internal void Prepare(string operation, string workerBuild, string? windowsJob, ITempFile? source, IWritableTempFile? destination, IReadOnlyList<ITempFile>? additionalSources = null)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var bindings = _aggregate.ToList();
            Add(source, "source");
            Add(destination, "output");
            foreach (var input in additionalSources ?? [])
                Add(input, "source");
            _record = _record with { Operation = operation, Phase = "prepared", WorkerBuild = workerBuild, WindowsJob = windowsJob, Identity = null, Staging = bindings };
            File.Delete(RecordPath + ".identity");
            MediaRunRecord.Write(RecordPath, _record);
            void Add(object? file, string role)
            {
                if (file is IMediaStagingBinding known)
                {
                    var binding = known.StagingBinding;
                    if (binding.JobId is not null && binding.JobId != JobId)
                        throw new InvalidDataException("Media staging and job identities differ.");
                    bindings.RemoveAll(previous => previous.Path == binding.Path);
                    bindings.Add(binding with { JobId = JobId });
                }
                else if (file is ITempFile input)
                    bindings.Add(new(Path.GetDirectoryName(input.Path)!, -1, input.Path, input.FileSize, JobId, null, role));
                else if (file is IWritableTempFile output)
                    bindings.Add(new(Path.GetDirectoryName(output.Path)!, -1, output.Path, output.MaximumBytes, JobId, null, role));
            }
        }
        internal void TrackAggregate(IMediaStagingAggregate aggregate)
        {
            if (!ProcessesStopped)
                throw new InvalidOperationException("Staging charges cannot change while a child is active.");
            var bindings = aggregate.StagingBindings;
            if (bindings.Any(binding => binding.JobId != JobId))
                throw new InvalidDataException("Aggregate and processing job identities differ.");
            _aggregate = bindings;
            _record = _record with { Staging = bindings };
            MediaRunRecord.Write(RecordPath, _record);
        }
        internal void MarkLaunching()
        {
            _record = _record with { Phase = "launching" };
            MediaRunRecord.Write(RecordPath, _record);
            _stopProof = false;
            _childActive = true;
        }
        internal bool ConfirmsStopped(MediaRunRecord record) => !_retained && !_childActive && _stopProof &&
            _record.RunId == record.RunId && _record.Phase == record.Phase && record.Phase is "prepared" or "stopped";
        internal async Task PermitAsync(Stream ready, Stream input, int expectedPid, CancellationToken ct, Func<Task>? identified = null)
        {
            byte[] buffer = new byte[33];
            int read = await ready.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
            if (read != buffer.Length || Encoding.ASCII.GetString(buffer) != Nonce + "\n")
                throw new InvalidDataException("The media child did not establish current custody.");
            var identity = ReadIdentity(RecordPath, Nonce, _record.Admission);
            if (identity is null || identity.ProcessId != expectedPid ||
                !OperatingSystem.IsWindows() && identity.GroupId != expectedPid || MediaProcessIdentity.Read(expectedPid) != identity)
                throw new InvalidDataException("The media child's durable process identity does not match.");
            if (identified is not null)
                await identified();
            _record = _record with { Phase = "permitted", Identity = identity };
            MediaRunRecord.Write(RecordPath, _record);
            await input.WriteAsync(Encoding.ASCII.GetBytes(Nonce + "\n"), ct);
            await input.FlushAsync(ct);
            input.Dispose();
        }
        internal void ConfirmStopped()
        {
            _childActive = false;
            _record = _record with { Phase = "stopped" };
            MediaRunRecord.Write(RecordPath, _record);
            File.Delete(RecordPath + ".identity");
            _stopProof = true;
        }
        internal void Retain() { _retained = true; Retained.TryAdd(RecordPath, this); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 || _retained)
                return;
            try
            {
                if (!_stopProof || _childActive || _record.Phase is not ("prepared" or "stopped"))
                { Retain(); return; }
                File.Delete(RecordPath + ".identity");
                File.Delete(RecordPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Retain(); return; }
            _owner.Dispose(); // Close only. The inherited OFD can outlive this parent.
            Owned.TryRemove(RecordPath, out _);
        }
        private sealed class Activation(MediaProcessingReservation? previous) : IDisposable
        { public void Dispose() => Ambient.Value = previous; }
    }
}
