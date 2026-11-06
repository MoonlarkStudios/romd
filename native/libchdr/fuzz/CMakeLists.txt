cmake_minimum_required(VERSION 3.24)
project(moonlark_chdr_fuzz LANGUAGES C)

# Builds the libFuzzer targets from the pinned libchdr sources with ROMD's feature options.
# scripts/native/fuzz.sh runs this inside the disposable container; see native/libchdr/FUZZING.md.
if(NOT CMAKE_C_COMPILER_ID MATCHES "Clang")
  message(FATAL_ERROR "libFuzzer targets need clang")
endif()
set(MOONLARK_UPSTREAM "${CMAKE_CURRENT_SOURCE_DIR}/../upstream" CACHE PATH "Pinned libchdr checkout")
if(NOT EXISTS "${MOONLARK_UPSTREAM}/include/libchdr/chd.h")
  message(FATAL_ERROR "No libchdr sources at ${MOONLARK_UPSTREAM}")
endif()

# The same feature settings as native/libchdr/CMakeLists.txt; keep them in sync. Assembly LZMA stays
# off because ROMD's build disables it and the sanitizers cannot instrument it.
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(INSTALL_STATIC_LIBS OFF CACHE BOOL "" FORCE)
set(WITH_SYSTEM_ZLIB OFF CACHE BOOL "" FORCE)
set(WITH_SYSTEM_ZSTD OFF CACHE BOOL "" FORCE)
set(CHDR_WANT_RAW_DATA_SECTOR ON CACHE BOOL "" FORCE)
set(CHDR_WANT_SUBCODE ON CACHE BOOL "" FORCE)
set(CHDR_VERIFY_BLOCK_CRC ON CACHE BOOL "" FORCE)
set(CHDR_LOWRAM_TARGET OFF CACHE BOOL "" FORCE)
set(CHDR_CD_SCRATCH_BUFFER ON CACHE STRING "" FORCE)
set(CHDR_FLAC_BACKEND drflac CACHE STRING "" FORCE)
set(CHDR_MICROFLAC_SOURCE_DIR "" CACHE PATH "" FORCE)
set(CHDR_WANT_TESTS OFF CACHE BOOL "" FORCE)
set(BUILD_LTO OFF CACHE BOOL "" FORCE)
set(WITH_LZMA_ASM OFF CACHE BOOL "" FORCE)
set(CMAKE_C_STANDARD 11 CACHE STRING "" FORCE)
set(CMAKE_C_STANDARD_REQUIRED ON CACHE BOOL "" FORCE)

# Every object, codecs included, is instrumented; undefined behaviour aborts like any other finding.
# Asserts stay enabled (no NDEBUG) so a violated codec invariant is reported where it happens.
add_compile_options(-O1 -g -fno-omit-frame-pointer -fsanitize=fuzzer-no-link,address,undefined
  -fno-sanitize-recover=undefined)

add_subdirectory("${MOONLARK_UPSTREAM}" upstream-build EXCLUDE_FROM_ALL)
get_target_property(upstream_sources chdr-static SOURCES)
list(TRANSFORM upstream_sources PREPEND "${MOONLARK_UPSTREAM}/")
get_target_property(upstream_defines chdr-static COMPILE_DEFINITIONS)
include("${CMAKE_CURRENT_SOURCE_DIR}/../HuffmanLookup.cmake")
set(moonlark_huffman "${CMAKE_CURRENT_BINARY_DIR}/patched/libchdr_huffman.c")
moonlark_patch_huffman("${MOONLARK_UPSTREAM}" "${moonlark_huffman}")
list(REMOVE_ITEM upstream_sources "${MOONLARK_UPSTREAM}/src/libchdr_huffman.c")
list(APPEND upstream_sources "${moonlark_huffman}")

function(moonlark_fuzz_target name)
  add_executable(${name} chd_walk_fuzzer.c ${ARGN} ${upstream_sources})
  target_include_directories(${name} PRIVATE "${MOONLARK_UPSTREAM}/include")
  target_compile_definitions(${name} PRIVATE ${upstream_defines})
  target_link_libraries(${name} PRIVATE chdr-lzma miniz zstd m)
  target_link_options(${name} PRIVATE -fsanitize=fuzzer,address,undefined)
endfunction()

# A build is either the fuzzing build or the sanitizer canary, never both. The canary turns the map guard off and adds an
# intentional signed overflow to show ASan and UBSan fire; scripts/native/fuzz.sh canary configures it in its own build
# directory, and the fuzzing build passes the option OFF explicitly.
option(CHDR_FUZZ_CANARY "Build only chd_walk_canary, the sanitizer canary that is never used to fuzz or replay" OFF)
if(CHDR_FUZZ_CANARY)
  moonlark_fuzz_target(chd_walk_canary ubsan_canary.c)
  target_compile_definitions(chd_walk_canary PRIVATE CHDR_FUZZ_SKIP_MAP_GUARD CHDR_FUZZ_UBSAN_CANARY)
else()
  moonlark_fuzz_target(chd_walk_fuzzer)
endif()
