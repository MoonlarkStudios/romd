using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Moonlark.Libchdr.Internal;
using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Qualifies that binding never depends on which managed code touches the library first.</summary>
/// <remarks>Each test loads a private copy of the assembly into a collectible context, whose statics start empty.</remarks>
public sealed class LoaderIsolationTests
{
    private const BindingFlags Statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>A raw import called before any LibchdrLibrary member still binds only through the verifying resolver.</summary>
    /// <remarks>The copied library beside the isolated assembly would satisfy default probing, unverified.</remarks>
    [Fact]
    public void FirstImportInAFreshLoadContextBindsOnlyThroughVerification() => InIsolation(isolated =>
    {
        TargetInvocationException call = Assert.Throws<TargetInvocationException>(() => CallErrorString(isolated));
        DllNotFoundException missing = Assert.IsType<DllNotFoundException>(call.InnerException);
        Assert.StartsWith("Packaged libchdr asset", missing.Message, StringComparison.Ordinal);
    });

    /// <summary>Initialization registers the resolver before it loads, so registration never waits for a raw import.</summary>
    [Fact]
    public void InitializationRegistersTheResolverEvenWhenLoadingFails() => InIsolation(isolated =>
    {
        MethodInfo initialize = Library(isolated).GetMethod("EnsureInitialized", Statics)!;
        TargetInvocationException call = Assert.Throws<TargetInvocationException>(() => initialize.Invoke(null, null));
        Assert.IsType<DllNotFoundException>(call.InnerException);
        Assert.Throws<InvalidOperationException>(() => NativeLibrary.SetDllImportResolver(isolated, static (_, _, _) => 0));
    });

    /// <summary>A resolver the host registered first fails closed, naming why, instead of binding unverified.</summary>
    [Fact]
    public void AHostResolverOnTheAssemblyFailsClosedWithAClearMessage() => InIsolation(isolated =>
    {
        NativeLibrary.SetDllImportResolver(isolated, static (_, _, _) => 0);
        TargetInvocationException call = Assert.Throws<TargetInvocationException>(() => CallErrorString(isolated));
        TypeInitializationException initializer = Assert.IsType<TypeInitializationException>(call.InnerException);
        InvalidOperationException conflict = Assert.IsType<InvalidOperationException>(initializer.InnerException);
        Assert.Contains("owns the DllImport resolver", conflict.Message, StringComparison.Ordinal);
    });

    /// <summary>A library that fails verification stays unloaded, so a later Load of the verified build succeeds.</summary>
    [Fact]
    public void FailedVerificationLeavesLoadRecoverable() => InIsolation(isolated =>
    {
        MethodInfo load = Library(isolated).GetMethod("Load", Statics)!;
        string foreign = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),
            (OperatingSystem.IsWindows() ? "" : "lib") + "clrjit" + Path.GetExtension(NativePlatform.Current().Filename));
        Assert.True(File.Exists(foreign), foreign);
        TargetInvocationException rejected = Assert.Throws<TargetInvocationException>(() => load.Invoke(null, [foreign]));
        Assert.IsType<BadImageFormatException>(rejected.InnerException);
        load.Invoke(null, [VerifiedLibrary()]);
        Assert.NotNull(Library(isolated).GetProperty("BuildInfo", Statics)!.GetValue(null));
    });

    /// <summary>The resident library is never released, so no finalizer can unload it under an open file.</summary>
    [Fact]
    public void OnlyOpenFilesOwnReleasableNativeHandles()
    {
        Type[] handles = typeof(LibchdrLibrary).Assembly.GetTypes().Where(typeof(SafeHandle).IsAssignableFrom).ToArray();
        Assert.Equal([typeof(ChdSafeHandle)], handles);
    }

    private static void InIsolation(Action<Assembly> test)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("moonlark-loader-");
        var context = new AssemblyLoadContext("loader-isolation", isCollectible: true);
        try
        {
            string assembly = Path.Combine(directory.FullName, Path.GetFileName(typeof(LibchdrLibrary).Assembly.Location));
            File.Copy(typeof(LibchdrLibrary).Assembly.Location, assembly);
            File.Copy(VerifiedLibrary(), Path.Combine(directory.FullName, NativePlatform.Current().Filename));
            test(context.LoadFromAssemblyPath(assembly));
        }
        finally
        {
            context.Unload();
            // Windows may keep the unloaded assembly mapped for a while; the copy is disposable either way.
            try
            { directory.Delete(recursive: true); }
            catch (Exception error) when (OperatingSystem.IsWindows() && error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static Type Library(Assembly isolated) => isolated.GetType(typeof(LibchdrLibrary).FullName!, throwOnError: true)!;

    private static object? CallErrorString(Assembly isolated)
    {
        MethodInfo errorString = isolated.GetType("Moonlark.Libchdr.Interop.NativeMethods", throwOnError: true)!
            .GetMethod("chd_error_string", Statics)!;
        return errorString.Invoke(null, [Enum.ToObject(errorString.GetParameters()[0].ParameterType, 0)]);
    }

    private static string VerifiedLibrary()
    {
        (string rid, string filename) = NativePlatform.Current();
        return Path.Combine(NativeTestEnvironment.Root, "artifacts", "native", "libchdr", rid, "native", filename);
    }
}
