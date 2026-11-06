cmake_minimum_required(VERSION 3.24)
project(moonlark_chdr LANGUAGES C)

# Static per-RID settings come from CMakePresets.json; the engineering
# `native build` command passes only verified paths and the selected compiler.
if(NOT MOONLARK_RID MATCHES "^(linux-x64|linux-arm64|osx-arm64)$")
  message(FATAL_ERROR "Select an explicit supported MOONLARK_RID through its configure preset")
endif()
if(NOT IS_DIRECTORY "${MOONLARK_UPSTREAM}" OR NOT IS_DIRECTORY "${MOONLARK_SOURCE_ROOT}" OR
   NOT EXISTS "${MOONLARK_BUILD_INFO_HEADER}" OR NOT MOONLARK_NATIVE_OUTPUT OR
   NOT MOONLARK_PROBE_OUTPUT)
  message(FATAL_ERROR "Use the engineering 'native build' command with verified sources and build-info")
endif()

# Never configure upstream's shared library: its wildcard export handling and
# install rules are not our ABI. Reuse its reviewed sources, defines and codecs.
# Every setting is a cache entry so the CMake File API records its effective value.
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
set(CMAKE_POSITION_INDEPENDENT_CODE ON CACHE BOOL "" FORCE)
set(CMAKE_C_VISIBILITY_PRESET hidden CACHE STRING "" FORCE)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON CACHE BOOL "" FORCE)
set(CMAKE_C_STANDARD 11 CACHE STRING "" FORCE)
set(CMAKE_C_STANDARD_REQUIRED ON CACHE BOOL "" FORCE)
if(MSVC)
  add_compile_options(/experimental:deterministic "/pathmap:${MOONLARK_SOURCE_ROOT}=/_/moonlark,${MOONLARK_UPSTREAM}=/_/moonlark/native/libchdr/upstream" /Brepro)
else()
  # Later maps win, so build-tree paths below the source root map to a fixed prefix.
  get_filename_component(moonlark_generated_dir "${MOONLARK_BUILD_INFO_HEADER}" DIRECTORY)
  foreach(kind file debug macro)
    add_compile_options("-f${kind}-prefix-map=${MOONLARK_SOURCE_ROOT}=/_/moonlark"
      "-f${kind}-prefix-map=${MOONLARK_UPSTREAM}=/_/moonlark/native/libchdr/upstream"
      "-f${kind}-prefix-map=${CMAKE_BINARY_DIR}=/_/build"
      "-f${kind}-prefix-map=${moonlark_generated_dir}=/_/generated")
  endforeach()
endif()

# The export allowlist is the only reviewed export input; each linker's list is derived from it.
set(moonlark_export_allowlist "${CMAKE_CURRENT_SOURCE_DIR}/exports.txt")
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${moonlark_export_allowlist}")
file(STRINGS "${moonlark_export_allowlist}" moonlark_exports)
set(moonlark_unique_exports ${moonlark_exports})
list(REMOVE_DUPLICATES moonlark_unique_exports)
if(NOT moonlark_unique_exports STREQUAL moonlark_exports OR NOT "moonlark_chdr_build_info" IN_LIST moonlark_exports)
  message(FATAL_ERROR "exports.txt must list unique exports including moonlark_chdr_build_info")
endif()
set(moonlark_def "LIBRARY moonlark_chdr\nEXPORTS\n")
set(moonlark_exported_symbols "")
set(moonlark_version_script "{\n  global:\n")
foreach(symbol IN LISTS moonlark_exports)
  if(NOT symbol MATCHES "^(chd_[a-z0-9_]+|moonlark_chdr_build_info)$")
    message(FATAL_ERROR "Unexpected export spelling in exports.txt: ${symbol}")
  endif()
  string(APPEND moonlark_def "    ${symbol}\n")
  string(APPEND moonlark_exported_symbols "_${symbol}\n")
  string(APPEND moonlark_version_script "    ${symbol};\n")
endforeach()
string(APPEND moonlark_version_script "  local: *;\n};\n")
set(moonlark_export_dir "${CMAKE_CURRENT_BINARY_DIR}/exports")
if(MSVC)
  set(moonlark_export_file "${moonlark_export_dir}/moonlark_chdr.def")
  file(WRITE "${moonlark_export_file}" "${moonlark_def}")
elseif(APPLE)
  set(moonlark_export_file "${moonlark_export_dir}/moonlark_chdr.exp")
  file(WRITE "${moonlark_export_file}" "${moonlark_exported_symbols}")
else()
  set(moonlark_export_file "${moonlark_export_dir}/moonlark_chdr.map")
  file(WRITE "${moonlark_export_file}" "${moonlark_version_script}")
endif()

add_subdirectory("${MOONLARK_UPSTREAM}" upstream-build EXCLUDE_FROM_ALL)
get_target_property(upstream_sources chdr-static SOURCES)
list(TRANSFORM upstream_sources PREPEND "${MOONLARK_UPSTREAM}/")
get_target_property(upstream_defines chdr-static COMPILE_DEFINITIONS)
include("${CMAKE_CURRENT_SOURCE_DIR}/HuffmanLookup.cmake")
set(moonlark_huffman "${CMAKE_CURRENT_BINARY_DIR}/patched/libchdr_huffman.c")
moonlark_patch_huffman("${MOONLARK_UPSTREAM}" "${moonlark_huffman}")
list(REMOVE_ITEM upstream_sources "${MOONLARK_UPSTREAM}/src/libchdr_huffman.c")
list(APPEND upstream_sources "${moonlark_huffman}")
add_library(moonlark_chdr SHARED ${upstream_sources} moonlark_chdr_build_info.c)
target_include_directories(moonlark_chdr PRIVATE "${MOONLARK_UPSTREAM}/include")
target_compile_definitions(moonlark_chdr PRIVATE ${upstream_defines} MOONLARK_CHDR_BUILDING
  "MOONLARK_CHDR_BUILD_INFO_HEADER=\"${MOONLARK_BUILD_INFO_HEADER}\"")
target_link_libraries(moonlark_chdr PRIVATE chdr-lzma miniz zstd)
foreach(codec chdr-lzma miniz zstd)
  set_target_properties(${codec} PROPERTIES POSITION_INDEPENDENT_CODE ON
    C_VISIBILITY_PRESET hidden VISIBILITY_INLINES_HIDDEN ON)
endforeach()
# zstd's public declarations override -fvisibility=hidden by default.
if(NOT MSVC)
  target_compile_definitions(zstd PRIVATE "ZSTDLIB_VISIBILITY=__attribute__((visibility(\"hidden\")))")
endif()
set_target_properties(moonlark_chdr PROPERTIES OUTPUT_NAME moonlark_chdr
  LIBRARY_OUTPUT_DIRECTORY "${MOONLARK_NATIVE_OUTPUT}"
  RUNTIME_OUTPUT_DIRECTORY "${MOONLARK_NATIVE_OUTPUT}"
  LINK_DEPENDS "${moonlark_export_file}")
if(MSVC)
  target_compile_definitions(moonlark_chdr PRIVATE CHD_DLL CHD_DLL_EXPORTS _CRT_SECURE_NO_WARNINGS)
  target_sources(moonlark_chdr PRIVATE "${moonlark_export_file}")
  target_link_options(moonlark_chdr PRIVATE /Brepro /INCREMENTAL:NO)
elseif(APPLE)
  # Apple ld rejects unresolved symbols by default. Its former explicit
  # '-undefined error' spelling is deprecated in the current linker.
  target_link_options(moonlark_chdr PRIVATE
    "LINKER:-exported_symbols_list,${moonlark_export_file}"
    "LINKER:-fatal_warnings" "LINKER:-dead_strip")
  set_target_properties(moonlark_chdr PROPERTIES MACOSX_RPATH OFF
    INSTALL_NAME_DIR "@loader_path" BUILD_WITH_INSTALL_NAME_DIR ON)
else()
  target_link_options(moonlark_chdr PRIVATE
    "LINKER:--version-script=${moonlark_export_file}"
    "LINKER:--no-undefined" "LINKER:--fatal-warnings")
  target_link_libraries(moonlark_chdr PRIVATE m)
  set_target_properties(moonlark_chdr PROPERTIES SKIP_BUILD_RPATH ON)
endif()

# Exercise the exact repaired source and feature definitions before a library
# build can succeed, including rebuilds that would otherwise reuse its objects.
add_executable(moonlark_huffman_probe EXCLUDE_FROM_ALL probe/huffman_lookup.c
  "${moonlark_huffman}" "${MOONLARK_UPSTREAM}/src/libchdr_bitstream.c")
target_include_directories(moonlark_huffman_probe PRIVATE "${MOONLARK_UPSTREAM}/include")
target_compile_definitions(moonlark_huffman_probe PRIVATE ${upstream_defines})
add_custom_target(moonlark_huffman_check
  COMMAND $<TARGET_FILE:moonlark_huffman_probe>
  DEPENDS moonlark_huffman_probe VERBATIM)
add_dependencies(moonlark_chdr moonlark_huffman_check)

# The ABI layout probe measures the pinned headers with the same compiler.
add_executable(moonlark_layout_probe "${MOONLARK_SOURCE_ROOT}/native/libchdr/probe/libchdr_layout.c")
target_include_directories(moonlark_layout_probe PRIVATE "${MOONLARK_UPSTREAM}/include")
if(MSVC)
  target_compile_options(moonlark_layout_probe PRIVATE /W4 /WX)
else()
  target_compile_options(moonlark_layout_probe PRIVATE -Wall -Wextra -Werror -pedantic)
endif()
set_target_properties(moonlark_layout_probe PROPERTIES OUTPUT_NAME layout-probe
  C_STANDARD 11 C_STANDARD_REQUIRED ON C_EXTENSIONS OFF EXPORT_COMPILE_COMMANDS ON
  RUNTIME_OUTPUT_DIRECTORY "${MOONLARK_PROBE_OUTPUT}")
