using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moonlark.Libchdr.Internal;
using Moonlark.Libchdr.Interop;
using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>
/// The raw ClangSharp output is committed verbatim, so its structural guarantees are checked on the compiled
/// assembly. These tests never load the native library.
/// </summary>
public sealed unsafe class InteropBoundaryTests
{
    private const string InteropNamespace = "Moonlark.Libchdr.Interop";
    private const string NativeLibraryName = "moonlark_chdr";
    private const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly Library = typeof(NativeMethods).Assembly;

    /// <summary>The managed shape of every pinned <c>chd.h</c> prototype: size_t is nuint, uint32_t is uint, int64_t is long, char is sbyte.</summary>
    private static readonly (string Name, Type Return, Type[] Parameters)[] Signatures =
    [
        ("chd_open_core_file_callbacks", typeof(chd_error), [typeof(core_file_callbacks*), typeof(void*), typeof(int), typeof(chd_file*), typeof(chd_file**)]),
        ("chd_open_core_file", typeof(chd_error), [typeof(core_file*), typeof(int), typeof(chd_file*), typeof(chd_file**)]),
        ("chd_open_file", typeof(chd_error), [typeof(void*), typeof(int), typeof(chd_file*), typeof(chd_file**)]),
        ("chd_open", typeof(chd_error), [typeof(sbyte*), typeof(int), typeof(chd_file*), typeof(chd_file**)]),
        ("chd_precache", typeof(chd_error), [typeof(chd_file*)]),
        ("chd_set_cache_budget", typeof(chd_error), [typeof(chd_file*), typeof(nuint)]),
        ("chd_get_cache_budget", typeof(nuint), [typeof(chd_file*)]),
        ("chd_get_cache_stats", typeof(void), [typeof(chd_file*), typeof(ulong*), typeof(ulong*)]),
        ("chd_close", typeof(void), [typeof(chd_file*)]),
        ("chd_core_file", typeof(core_file*), [typeof(chd_file*)]),
        ("chd_error_string", typeof(sbyte*), [typeof(chd_error)]),
        ("chd_get_header", typeof(chd_header*), [typeof(chd_file*)]),
        ("chd_read_header_core_file_callbacks", typeof(chd_error), [typeof(core_file_callbacks*), typeof(void*), typeof(chd_header*)]),
        ("chd_read_header_core_file", typeof(chd_error), [typeof(core_file*), typeof(chd_header*)]),
        ("chd_read_header_file", typeof(chd_error), [typeof(void*), typeof(chd_header*)]),
        ("chd_read_header", typeof(chd_error), [typeof(sbyte*), typeof(chd_header*)]),
        ("chd_read", typeof(chd_error), [typeof(chd_file*), typeof(uint), typeof(void*)]),
        ("chd_get_metadata", typeof(chd_error), [typeof(chd_file*), typeof(uint), typeof(uint), typeof(void*), typeof(uint), typeof(uint*), typeof(uint*), typeof(byte*)]),
        ("moonlark_chdr_build_info", typeof(sbyte*), []),
    ];

    private static readonly Type[] BlittableScalars =
        [typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(nint), typeof(nuint)];

    /// <summary>Every import has exactly the pinned header's fixed-width managed signature, so regeneration cannot silently change a width.</summary>
    [Fact]
    public void NativeMethodsMatchThePinnedHeaderWidths()
    {
        Dictionary<string, MethodInfo> imports = PInvokes(typeof(NativeMethods)).ToDictionary(method => method.Name, StringComparer.Ordinal);
        Assert.Equal(Signatures.Select(signature => signature.Name).Order(StringComparer.Ordinal), imports.Keys.Order(StringComparer.Ordinal));
        foreach ((string name, Type returns, Type[] parameters) in Signatures)
        {
            MethodInfo method = imports[name];
            Assert.True(returns == method.ReturnType, $"{name} returns {method.ReturnType}, expected {returns}");
            Assert.Equal(parameters, method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        }
    }

    /// <summary>Every P/Invoke in the assembly passes only blittable scalars, four-byte enums, pointers to unmanaged types and function pointers.</summary>
    [Fact]
    public void EveryPInvokeSignatureIsBlittable()
    {
        MethodInfo[] methods = Library.GetTypes().SelectMany(PInvokes).ToArray();
        Assert.NotEmpty(methods);
        foreach (MethodInfo method in methods)
        {
            Assert.True(method.ReturnType == typeof(void) || IsBlittable(method.ReturnType), $"{method.Name} returns {method.ReturnType}");
            foreach (ParameterInfo parameter in method.GetParameters())
                Assert.True(IsBlittable(parameter.ParameterType), $"{method.Name}({parameter.Name}) is {parameter.ParameterType}");
        }
    }

    private static bool IsBlittable(Type type)
    {
        if (type.IsByRef || type.IsArray)
            return false;
        if (type.IsFunctionPointer)
            return true;
        if (type.IsPointer)
        {
            Type element = type.GetElementType()!;
            return element == typeof(void) || IsBlittable(element) || (element.IsValueType && !element.IsEnum && !ContainsReferences(element));
        }
        if (type.IsEnum)
            return Marshal.SizeOf(Enum.GetUnderlyingType(type)) == 4;
        return BlittableScalars.Contains(type);
    }

    private static bool ContainsReferences(Type type) => (bool)typeof(RuntimeHelpers)
        .GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!.MakeGenericMethod(type).Invoke(null, null)!;

    /// <summary>Every raw type, including nested fixed buffers and generated helpers, is invisible outside the assembly.</summary>
    [Fact]
    public void EveryInteropTypeIsNonPublic()
    {
        Type[] types = Library.GetTypes().Where(type => type.Namespace == InteropNamespace).ToArray();
        Assert.Contains(typeof(NativeMethods), types);
        Assert.All(types, type => Assert.False(type.IsVisible, type.FullName));
        Assert.All(types.Where(type => !type.IsNested), type => Assert.True(type.IsNotPublic, type.FullName));
    }

    /// <summary>NativeMethods imports exactly the paired export inventory, each a raw Cdecl exact-spelling import of the native library.</summary>
    [Fact]
    public void NativeMethodsImportExactlyThePairedExports()
    {
        DllImportAttribute[] imports = PInvokes(typeof(NativeMethods)).Select(Import).ToArray();
        string[] exports = NativeBuildContract.Exports.ToArray();
        Assert.Equal(exports.Order(StringComparer.Ordinal), imports.Select(import => import.EntryPoint ?? "").Order(StringComparer.Ordinal));
        Assert.All(imports, import =>
        {
            Assert.Equal(NativeLibraryName, import.Value);
            Assert.Equal(CallingConvention.Cdecl, import.CallingConvention);
            Assert.True(import.ExactSpelling, import.EntryPoint);
        });
    }

    /// <summary>No P/Invoke anywhere in the assembly, including compiler-generated stubs, targets another library.</summary>
    [Fact]
    public void NoImportTargetsAnotherLibrary()
    {
        MethodInfo[] methods = Library.GetTypes().SelectMany(PInvokes).ToArray();
        Assert.NotEmpty(methods);
        Assert.All(methods, method => Assert.Equal(NativeLibraryName, Import(method).Value));
    }

    /// <summary>Every P/Invoke is declared on NativeMethods, whose type initializer registers the verifying resolver.</summary>
    [Fact]
    public void EveryImportIsDeclaredOnNativeMethods()
    {
        MethodInfo[] methods = Library.GetTypes().SelectMany(PInvokes).ToArray();
        Assert.NotEmpty(methods);
        Assert.All(methods, method => Assert.Equal(typeof(NativeMethods), method.DeclaringType));
    }

    private static IEnumerable<MethodInfo> PInvokes(Type type) =>
        type.GetMethods(Declared).Where(method => (method.Attributes & MethodAttributes.PinvokeImpl) != 0);

    private static DllImportAttribute Import(MethodInfo method)
    {
        DllImportAttribute? import = method.GetCustomAttribute<DllImportAttribute>();
        Assert.NotNull(import);
        return import;
    }
}
