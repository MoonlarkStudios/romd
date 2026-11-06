#ifndef MOONLARK_CHDR_BUILD_INFO_H
#define MOONLARK_CHDR_BUILD_INFO_H

#if defined(_WIN32) && defined(MOONLARK_CHDR_BUILDING)
#define MOONLARK_CHDR_API __declspec(dllexport)
#elif !defined(_WIN32)
#define MOONLARK_CHDR_API __attribute__((visibility("default")))
#else
#define MOONLARK_CHDR_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Returns immutable, NUL-terminated UTF-8 JSON owned by the library.
 * The buffer, including its terminator, is at most 16384 bytes. Its lifetime
 * is the lifetime of the loaded library; callers must never free or write it.
 * schemaVersion/abiVersion are 1. Fields are managedVersion, nativeVersion,
 * upstreamVersion, upstreamCommit, headers (path -> SHA-256), features, buildId.
 * buildId identifies the canonical source/flags/toolchain/RID/wrapper recipe,
 * not a hash of the binary containing this string. */
MOONLARK_CHDR_API const char *moonlark_chdr_build_info(void);

#ifdef __cplusplus
}
#endif

#endif
