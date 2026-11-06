/*
 * The UndefinedBehaviorSanitizer canary, linked only into chd_walk_canary (CMake option CHDR_FUZZ_CANARY), never into
 * chd_walk_fuzzer. scripts/native/fuzz.sh canary feeds it an input that starts with UBSAN_CANARY_TRIGGER and requires
 * UBSan's signed-overflow report and abort, showing that undefined behaviour is instrumented and fatal in this toolchain.
 */
#ifndef CHDR_FUZZ_UBSAN_CANARY
#error "ubsan_canary.c belongs to the chd_walk_canary target only."
#endif

#include <limits.h>
#include <stddef.h>

void chd_fuzz_ubsan_canary(size_t size);

static volatile int sink;

void chd_fuzz_ubsan_canary(size_t size)
{
    /* The addend depends on the input, and the base is volatile, so the compiler cannot fold the overflow away. */
    volatile int base = INT_MAX;
    sink = base + (int)(size != 0);
}
