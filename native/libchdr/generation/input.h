#include <libchdr/coretypes.h>
#if defined(CHD_DLL) || defined(MOONLARK_CHDR_BUILDING)
#error Binding generation must use declaration-only exports
#endif
#pragma push_macro("_MSC_VER")
#undef _MSC_VER
#define _MSC_VER 1
#include <libchdr/chd.h>
#pragma pop_macro("_MSC_VER")
#pragma push_macro("_WIN32")
#undef _WIN32
#define _WIN32 1
#include "../moonlark_chdr_build_info.h"
#pragma pop_macro("_WIN32")
