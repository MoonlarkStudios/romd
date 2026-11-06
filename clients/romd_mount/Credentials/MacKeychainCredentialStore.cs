using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Romd.Mount.Credentials;

/// <summary>
/// Local generic-password items in Mount's private service namespace. Uses SecItem APIs;
/// never runs a shell or puts secrets in arguments. Native operations finish once started.
/// Interaction-required errors are returned instead of displaying a keychain prompt.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainCredentialStore : IMountCredentialStore
{
    private const string Service = "com.romd.mount.refresh-token.v1";
    private const int NotFound = -25300;
    private const int Duplicate = -25299;

    public Task<string?> ReadAsync(MountCredentialScope scope, CancellationToken ct = default) => Run(() =>
    {
        using var query = Query(scope);
        query.Set("kSecReturnData", Native.Constant(Native.Core, "kCFBooleanTrue"));
        query.Set("kSecMatchLimit", Native.Constant(Native.Security, "kSecMatchLimitOne"));
        int status = Native.SecItemCopyMatching(query.Handle, out nint result);
        if (status == NotFound) return null;
        Check(status);
        try
        {
            nint length = Native.CFDataGetLength(result);
            if (length is <= 0 or > 32768) throw new MountCredentialException(-1);
            byte[] bytes = new byte[(int)length];
            try
            {
                Marshal.Copy(Native.CFDataGetBytePtr(result), bytes, 0, bytes.Length);
                string value = new UTF8Encoding(false, true).GetString(bytes);
                if (value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))) throw new MountCredentialException(-1);
                return value;
            }
            catch (DecoderFallbackException) { throw new MountCredentialException(-1); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { Native.CFRelease(result); }
    }, ct);

    public Task WriteAsync(MountCredentialScope scope, string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(refreshToken) || refreshToken.Length > 32768 ||
            refreshToken.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new ArgumentException("Invalid refresh token.", nameof(refreshToken));
        return Run(() =>
        {
            using var query = Query(scope);
            using var update = new Dictionary();
            byte[] bytes = Encoding.UTF8.GetBytes(refreshToken);
            if (bytes.Length > 32768) { CryptographicOperations.ZeroMemory(bytes); throw new MountCredentialException(-1); }
            nint data;
            try { data = Native.CFDataCreate(0, bytes, bytes.Length); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            if (data == 0) throw new MountCredentialException(-1);
            try
            {
                update.Set("kSecValueData", data);
                int status = Native.SecItemUpdate(query.Handle, update.Handle);
                if (status == NotFound)
                {
                    query.Set("kSecValueData", data);
                    status = Native.SecItemAdd(query.Handle, 0);
                    if (status == Duplicate)
                    {
                        using var retry = Query(scope);
                        status = Native.SecItemUpdate(retry.Handle, update.Handle);
                    }
                }
                Check(status);
                return true;
            }
            finally { Native.CFRelease(data); }
        }, ct);
    }

    public Task DeleteAsync(MountCredentialScope scope, CancellationToken ct = default) => Run(() =>
    {
        using var query = Query(scope);
        int status = Native.SecItemDelete(query.Handle);
        if (status != NotFound) Check(status);
        return true;
    }, ct);

    private static Task<T> Run<T>(Func<T> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        // Cancel only before dispatch. Do not report cancellation after a native write committed.
        return Task.Run(() =>
        {
            // The legacy macOS keychain backend can ignore the per-query UI
            // policy during an application's ACL check. This headless client
            // must also disable legacy interaction at process scope.
            Check(Native.SecKeychainSetUserInteractionAllowed(0));
            return action();
        });
    }

    private static Dictionary Query(MountCredentialScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var query = new Dictionary();
        query.Set("kSecClass", Native.Constant(Native.Security, "kSecClassGenericPassword"));
        query.SetText("kSecAttrService", Service);
        query.SetText("kSecAttrAccount", scope.Key);
        query.Set("kSecAttrSynchronizable", Native.Constant(Native.Core, "kCFBooleanFalse"));
        query.Set("kSecUseAuthenticationUI", Native.Constant(Native.Security, "kSecUseAuthenticationUIFail"));
        return query;
    }

    private static void Check(int status) { if (status != 0) throw new MountCredentialException(status); }

    private sealed class Dictionary : IDisposable
    {
        public nint Handle { get; } = Native.CFDictionaryCreateMutable(0, 0,
            NativeLibrary.GetExport(Native.Core, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(Native.Core, "kCFTypeDictionaryValueCallBacks"));
        public Dictionary() { if (Handle == 0) throw new MountCredentialException(-1); }
        public void Set(string key, nint value) => Native.CFDictionarySetValue(Handle, Native.Constant(Native.Security, key), value);
        public void SetText(string key, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            nint text = Native.CFStringCreateWithBytes(0, bytes, bytes.Length, 0x08000100, 0);
            if (text == 0) throw new MountCredentialException(-1);
            try { Set(key, text); }
            finally { Native.CFRelease(text); }
        }
        public void Dispose() => Native.CFRelease(Handle);
    }

    private static class Native
    {
        private const string SecurityPath = "/System/Library/Frameworks/Security.framework/Security";
        private const string CorePath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        internal static readonly nint Security = NativeLibrary.Load(SecurityPath);
        internal static readonly nint Core = NativeLibrary.Load(CorePath);
        internal static nint Constant(nint library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
        [DllImport(SecurityPath)] internal static extern int SecItemCopyMatching(nint query, out nint result);
        [DllImport(SecurityPath)] internal static extern int SecItemAdd(nint attributes, nint result);
        [DllImport(SecurityPath)] internal static extern int SecItemUpdate(nint query, nint attributes);
        [DllImport(SecurityPath)] internal static extern int SecItemDelete(nint query);
        [DllImport(SecurityPath)] internal static extern int SecKeychainSetUserInteractionAllowed(byte allowed);
        [DllImport(CorePath)] internal static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keyCallbacks, nint valueCallbacks);
        [DllImport(CorePath)] internal static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);
        [DllImport(CorePath)] internal static extern nint CFStringCreateWithBytes(nint allocator, byte[] bytes, nint length, uint encoding, byte external);
        [DllImport(CorePath)] internal static extern nint CFDataCreate(nint allocator, byte[] bytes, nint length);
        [DllImport(CorePath)] internal static extern nint CFDataGetLength(nint data);
        [DllImport(CorePath)] internal static extern nint CFDataGetBytePtr(nint data);
        [DllImport(CorePath)] internal static extern void CFRelease(nint value);
    }
}
