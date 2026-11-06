using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: GeneratedCode("ClangSharp", "21.1.8.4")]

namespace Moonlark.Libchdr.Interop;

internal unsafe partial struct core_file_callbacks
{
    [NativeTypeName("uint64_t (*)(void *)")]
    public delegate* unmanaged[Cdecl]<void*, ulong> fsize;

    [NativeTypeName("size_t (*)(void *, size_t, size_t, void *)")]
    public delegate* unmanaged[Cdecl]<void*, nuint, nuint, void*, nuint> fread;

    [NativeTypeName("int (*)(void *)")]
    public delegate* unmanaged[Cdecl]<void*, int> fclose;

    [NativeTypeName("int (*)(void *, int64_t, int)")]
    public delegate* unmanaged[Cdecl]<void*, long, int, int> fseek;
}

internal unsafe partial struct core_file_callbacks_and_argp
{
    [NativeTypeName("const core_file_callbacks *")]
    public core_file_callbacks* callbacks;

    public void* argp;
}

internal unsafe partial struct core_file
{
    public void* argp;

    [NativeTypeName("uint64_t (*)(struct chd_core_file *)")]
    public delegate* unmanaged[Cdecl]<core_file*, ulong> fsize;

    [NativeTypeName("size_t (*)(void *, size_t, size_t, struct chd_core_file *)")]
    public delegate* unmanaged[Cdecl]<void*, nuint, nuint, core_file*, nuint> fread;

    [NativeTypeName("int (*)(struct chd_core_file *)")]
    public delegate* unmanaged[Cdecl]<core_file*, int> fclose;

    [NativeTypeName("int (*)(struct chd_core_file *, int64_t, int)")]
    public delegate* unmanaged[Cdecl]<core_file*, long, int, int> fseek;
}

internal enum chd_error
{
    CHDERR_NONE,
    CHDERR_NO_INTERFACE,
    CHDERR_OUT_OF_MEMORY,
    CHDERR_INVALID_FILE,
    CHDERR_INVALID_PARAMETER,
    CHDERR_INVALID_DATA,
    CHDERR_FILE_NOT_FOUND,
    CHDERR_REQUIRES_PARENT,
    CHDERR_FILE_NOT_WRITEABLE,
    CHDERR_READ_ERROR,
    CHDERR_WRITE_ERROR,
    CHDERR_CODEC_ERROR,
    CHDERR_INVALID_PARENT,
    CHDERR_HUNK_OUT_OF_RANGE,
    CHDERR_DECOMPRESSION_ERROR,
    CHDERR_COMPRESSION_ERROR,
    CHDERR_CANT_CREATE_FILE,
    CHDERR_CANT_VERIFY,
    CHDERR_NOT_SUPPORTED,
    CHDERR_METADATA_NOT_FOUND,
    CHDERR_INVALID_METADATA_SIZE,
    CHDERR_UNSUPPORTED_VERSION,
    CHDERR_VERIFY_INCOMPLETE,
    CHDERR_INVALID_METADATA,
    CHDERR_INVALID_STATE,
    CHDERR_OPERATION_PENDING,
    CHDERR_NO_ASYNC_OPERATION,
    CHDERR_UNSUPPORTED_FORMAT,
}

internal partial struct chd_file
{
}

internal unsafe partial struct chd_header
{
    [NativeTypeName("uint32_t")]
    public uint length;

    [NativeTypeName("uint32_t")]
    public uint version;

    [NativeTypeName("uint32_t")]
    public uint flags;

    [NativeTypeName("uint32_t[4]")]
    public _compression_e__FixedBuffer compression;

    [NativeTypeName("uint32_t")]
    public uint hunkbytes;

    [NativeTypeName("uint32_t")]
    public uint totalhunks;

    [NativeTypeName("uint64_t")]
    public ulong logicalbytes;

    [NativeTypeName("uint64_t")]
    public ulong metaoffset;

    [NativeTypeName("uint64_t")]
    public ulong mapoffset;

    [NativeTypeName("uint8_t[16]")]
    public _md5_e__FixedBuffer md5;

    [NativeTypeName("uint8_t[16]")]
    public _parentmd5_e__FixedBuffer parentmd5;

    [NativeTypeName("uint8_t[20]")]
    public _sha1_e__FixedBuffer sha1;

    [NativeTypeName("uint8_t[20]")]
    public _rawsha1_e__FixedBuffer rawsha1;

    [NativeTypeName("uint8_t[20]")]
    public _parentsha1_e__FixedBuffer parentsha1;

    [NativeTypeName("uint32_t")]
    public uint unitbytes;

    [NativeTypeName("uint64_t")]
    public ulong unitcount;

    [NativeTypeName("uint32_t")]
    public uint hunkcount;

    [NativeTypeName("uint32_t")]
    public uint mapentrybytes;

    [NativeTypeName("uint8_t *")]
    public byte* rawmap;

    [NativeTypeName("uint32_t")]
    public uint obsolete_cylinders;

    [NativeTypeName("uint32_t")]
    public uint obsolete_sectors;

    [NativeTypeName("uint32_t")]
    public uint obsolete_heads;

    [NativeTypeName("uint32_t")]
    public uint obsolete_hunksize;

    [InlineArray(4)]
    public partial struct _compression_e__FixedBuffer
    {
        public uint e0;
    }

    [InlineArray(16)]
    public partial struct _md5_e__FixedBuffer
    {
        public byte e0;
    }

    [InlineArray(16)]
    public partial struct _parentmd5_e__FixedBuffer
    {
        public byte e0;
    }

    [InlineArray(20)]
    public partial struct _sha1_e__FixedBuffer
    {
        public byte e0;
    }

    [InlineArray(20)]
    public partial struct _rawsha1_e__FixedBuffer
    {
        public byte e0;
    }

    [InlineArray(20)]
    public partial struct _parentsha1_e__FixedBuffer
    {
        public byte e0;
    }
}

internal partial struct _chd_verify_result
{
    [NativeTypeName("uint8_t[16]")]
    public _md5_e__FixedBuffer md5;

    [NativeTypeName("uint8_t[20]")]
    public _sha1_e__FixedBuffer sha1;

    [NativeTypeName("uint8_t[20]")]
    public _rawsha1_e__FixedBuffer rawsha1;

    [NativeTypeName("uint8_t[20]")]
    public _metasha1_e__FixedBuffer metasha1;

    [InlineArray(16)]
    public partial struct _md5_e__FixedBuffer
    {
        public byte e0;
    }

    [InlineArray(20)]
    public partial struct _sha1_e__FixedBuffer
    {
        public byte e0;
    }

    [InlineArray(20)]
    public partial struct _rawsha1_e__FixedBuffer
    {
        public byte e0;
    }

    [InlineArray(20)]
    public partial struct _metasha1_e__FixedBuffer
    {
        public byte e0;
    }
}

internal static unsafe partial class NativeMethods
{
    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern chd_error chd_open_core_file_callbacks([NativeTypeName("const core_file_callbacks *")] core_file_callbacks* callbacks, [NativeTypeName("const void *")] void* user_data, int mode, chd_file* parent, chd_file** chd);

    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern chd_error chd_open_core_file(core_file* file, int mode, chd_file* parent, chd_file** chd);

    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern chd_error chd_open_file([NativeTypeName("FILE *")] void* file, int mode, chd_file* parent, chd_file** chd);

    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern chd_error chd_open([NativeTypeName("const char *")] sbyte* filename, int mode, chd_file* parent, chd_file** chd);

    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern chd_error chd_precache(chd_file* chd);

    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern chd_error chd_set_cache_budget(chd_file* chd, [NativeTypeName("size_t")] nuint bytes);

    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("size_t")]
    public static extern nuint chd_get_cache_budget([NativeTypeName("const chd_file *")] chd_file* chd);

    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void chd_get_cache_stats([NativeTypeName("const chd_file *")] chd_file* chd, [NativeTypeName("uint64_t *")] ulong* hits, [NativeTypeName("uint64_t *")] ulong* misses);

    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void chd_close(chd_file* chd);

    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern core_file* chd_core_file(chd_file* chd);

    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("const char *")]
    public static extern sbyte* chd_error_string(chd_error err);

    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("const chd_header *")]
    public static extern chd_header* chd_get_header(chd_file* chd);

    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern chd_error chd_read_header_core_file_callbacks([NativeTypeName("const core_file_callbacks *")] core_file_callbacks* callback, [NativeTypeName("const void *")] void* user_data, chd_header* header);

    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern chd_error chd_read_header_core_file(core_file* file, chd_header* header);

    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern chd_error chd_read_header_file([NativeTypeName("FILE *")] void* file, chd_header* header);

    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern chd_error chd_read_header([NativeTypeName("const char *")] sbyte* filename, chd_header* header);

    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern chd_error chd_read(chd_file* chd, [NativeTypeName("uint32_t")] uint hunknum, void* buffer);

    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern chd_error chd_get_metadata(chd_file* chd, [NativeTypeName("uint32_t")] uint searchtag, [NativeTypeName("uint32_t")] uint searchindex, void* output, [NativeTypeName("uint32_t")] uint outputlen, [NativeTypeName("uint32_t *")] uint* resultlen, [NativeTypeName("uint32_t *")] uint* resulttag, [NativeTypeName("uint8_t *")] byte* resultflags);

    [DllImport("moonlark_chdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("const char *")]
    public static extern sbyte* moonlark_chdr_build_info();
}

/// <summary>Defines the type of a member as it was used in the native signature.</summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = false, Inherited = true)]
[Conditional("DEBUG")]
internal sealed partial class NativeTypeNameAttribute : Attribute
{
    private readonly string _name;

    /// <summary>Initializes a new instance of the <see cref="NativeTypeNameAttribute" /> class.</summary>
    /// <param name="name">The name of the type that was used in the native signature.</param>
    public NativeTypeNameAttribute(string name)
    {
        _name = name;
    }

    /// <summary>Gets the name of the type that was used in the native signature.</summary>
    public string Name => _name;
}

/// <summary>Defines the annotation found in a native declaration.</summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = true, Inherited = false)]
[Conditional("DEBUG")]
internal sealed partial class NativeAnnotationAttribute : Attribute
{
    private readonly string _annotation;

    /// <summary>Initializes a new instance of the <see cref="NativeAnnotationAttribute" /> class.</summary>
    /// <param name="annotation">The annotation that was used in the native declaration.</param>
    public NativeAnnotationAttribute(string annotation)
    {
        _annotation = annotation;
    }

    /// <summary>Gets the annotation that was used in the native declaration.</summary>
    public string Annotation => _annotation;
}
