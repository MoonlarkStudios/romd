#include "moonlark_chdr_build_info.h"
#include MOONLARK_CHDR_BUILD_INFO_HEADER

#if !WANT_RAW_DATA_SECTOR || !WANT_SUBCODE || !VERIFY_BLOCK_CRC
#error "The package requires raw sectors, subcode and block CRC verification."
#endif
#if LOWRAM_TARGET || !CHDR_CD_SCRATCH_BUFFER
#error "The package requires full maps and private CD scratch buffers."
#endif

static const char build_info[] = MOONLARK_CHDR_BUILD_INFO_JSON;
_Static_assert(sizeof(build_info) <= 16384, "build-info exceeds the ABI limit");

MOONLARK_CHDR_API const char *moonlark_chdr_build_info(void)
{
    return build_info;
}
