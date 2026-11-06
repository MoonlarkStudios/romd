namespace Romd.Infrastructure.Storage;

/// <summary>Tracks startup verification of explicitly configured CHD native builds.</summary>
public sealed class ChdDeploymentRuntime
{
    private readonly bool _requireTool;
    private int _ready;

    public ChdDeploymentRuntime(NativeToolOptions options, bool requireTool)
    {
        _requireTool = requireTool;
        Assets = ChdDeploymentAssets.Create(options, requireTool);
    }

    public ChdDeploymentAssets? Assets { get; }
    public bool IsReady => Volatile.Read(ref _ready) == 1;

    public void Verify(CancellationToken ct = default)
    {
        Invalidate();
        try
        {
            ct.ThrowIfCancellationRequested();
            if (Assets is { } assets)
            {
                assets.Native.VerifyAndLoad(ct);
                if (_requireTool)
                    assets.Tool!.Verify(ct);
            }
            ct.ThrowIfCancellationRequested();
            Volatile.Write(ref _ready, 1);
        }
        catch
        {
            Invalidate();
            throw;
        }
    }

    public void Invalidate() => Volatile.Write(ref _ready, 0);
}
