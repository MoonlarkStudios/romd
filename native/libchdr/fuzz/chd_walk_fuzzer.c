/*
 * libFuzzer target for the pinned libchdr, driven the way Moonlark.Libchdr's ChdFile drives it.
 *
 * Each input is a whole CHD, opened from memory through chd_open_core_file_callbacks. The target walks
 * the header, the map and the metadata, then decodes a bounded set of hunks into a buffer of exactly
 * hunkbytes. Before libchdr reads a hunk, the input must pass the checks Moonlark.Libchdr applies first
 * (ChdHeaderReader.ValidateEnvelope, ChdHeaderReader.Snapshot and ChdMapValidator), because those checks
 * exist to keep known libchdr defects away from chd_read. libchdr therefore sees the inputs Moonlark.Libchdr
 * would pass to it; findings inside this target's own chd_get_metadata calls need separate reachability triage.
 * Keep the mirrored checks in sync with their C# counterparts named below.
 *
 * Replay is deterministic: the read-ahead budget and the hunk order derive from the input size only.
 * Set CHDR_FUZZ_VERBOSE=1 to print one disposition line per input. scripts/native/fuzz.sh parity parses these lines
 * (tests/Moonlark.Libchdr.Tests/GuardParity.cs); change both together:
 *   chd_walk: reject stage=<envelope|bound|open|validation> size=<bytes> [hunkbytes=<n> hunks=<n>] reason=<text>
 *   chd_walk: accept size=<bytes> version=<n> codecs=<c0,c1,c2,c3> hunkbytes=<n> hunks=<n> metadata=<n> budget=<n>
 *             order=<forward|reverse> read=<n> decoded=<n>
 * Only the bound rejection carries hunkbytes and hunks: the raw header geometry the harness bound refused.
 *
 * The chd_walk_canary target (CMake option CHDR_FUZZ_CANARY, scripts/native/fuzz.sh canary) compiles this file with
 * CHDR_FUZZ_SKIP_MAP_GUARD and CHDR_FUZZ_UBSAN_CANARY to show the sanitizers fire. It is never used to fuzz or replay.
 */
#if !WANT_RAW_DATA_SECTOR || !WANT_SUBCODE || !VERIFY_BLOCK_CRC
#error "The fuzz build requires raw sectors, subcode and block CRC verification, like ROMD's build."
#endif
#if LOWRAM_TARGET || !CHDR_CD_SCRATCH_BUFFER
#error "The fuzz build requires full maps and private CD scratch buffers, like ROMD's build."
#endif
#if defined(CHDR_FUZZ_SKIP_MAP_GUARD) != defined(CHDR_FUZZ_UBSAN_CANARY)
#error "The canary macros come together, from the chd_walk_canary target only (CMake option CHDR_FUZZ_CANARY)."
#endif

#include <libchdr/chd.h>

#include <inttypes.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef CHDR_FUZZ_UBSAN_CANARY
/* ubsan_canary.c, linked only into chd_walk_canary: an input starting with this text runs an intentional signed overflow. */
#define UBSAN_CANARY_TRIGGER "ROMD-UBSAN-CANARY"
void chd_fuzz_ubsan_canary(size_t size);
#endif

/* Header bounds equal to ROMD's CHD preflight limits (ChdDvdLimits in src/Romd.Media/PlayStation); never lower them. */
#define MAX_HUNK_BYTES (1u << 20)        /* above real hunks: 16,384 (DVD), 19,584 (CD), 223,668 (AVHuff) */
#define MAX_HUNK_COUNT 2621440u
/* Walk bounds that keep iterations fast and memory bounded. */
#define MAX_HUNKS_READ 64u
#define MAX_DECODED_BYTES (4u << 20)
#define MAX_METADATA_ENTRIES 64u
#define METADATA_BUFFER_BYTES 4096u
#define WHOLE_FILE_READ_AHEAD (1u << 20) /* the corpus runner's 1 MiB window */

/* Moonlark.Libchdr constants. */
#define MAX_METADATA_CHAIN 65536u        /* ChdMetadataIndex.Read */
#define MAX_SELF_REFERENCE_DEPTH 16u     /* ChdMapValidator.MaxSelfReferenceDepth */
#define CODEC_AVHUFF 0x61766875u         /* 'avhu' */
#define V5_ENTRY_BYTES 12u
#define V5_LAST_CODEC_SLOT 3u
#define V5_STORED 4u
#define V5_SELF 5u
#define V5_PARENT 6u
#define LEGACY_ZLIB 1u
#define LEGACY_ZLIB_PLUS 2u
#define LEGACY_AV 3u
#define LEGACY_COMPRESSED 1u
#define LEGACY_UNCOMPRESSED 2u
#define LEGACY_MINI 3u
#define LEGACY_SELF 4u
#define LEGACY_PARENT 5u
#define MINI_BYTES 8u

static bool verbose;

/* ---- the in-memory source, with ChdSourceContext's callback semantics ---- */

typedef struct {
    const uint8_t *data;
    uint64_t size;
    uint64_t position;
} memory_source;

static uint64_t source_size(void *user_data) { return ((const memory_source *)user_data)->size; }

static size_t source_read(void *buffer, size_t size, size_t count, void *user_data)
{
    memory_source *source = user_data;
    if (size == 0 || count == 0 || size > SIZE_MAX / count || source->position >= source->size) return 0;
    uint64_t wanted = (uint64_t)size * count;
    uint64_t available = source->size - source->position;
    size_t copied = (size_t)(wanted < available ? wanted : available);
    memcpy(buffer, source->data + source->position, copied);
    source->position += copied;
    return copied / size;
}

/* libchdr calls this on failed opens as well as from chd_close; the input belongs to libFuzzer. */
static int source_close(void *user_data) { (void)user_data; return 0; }

static int source_seek(void *user_data, int64_t offset, int origin)
{
    memory_source *source = user_data;
    int64_t basis;
    switch (origin)
    {
        case SEEK_SET: basis = 0; break;
        case SEEK_CUR: basis = (int64_t)source->position; break;
        case SEEK_END: basis = (int64_t)source->size; break;
        default: return -1;
    }
    int64_t target;
    if (__builtin_add_overflow(basis, offset, &target) || target < 0) return -1;
    source->position = (uint64_t)target;
    return 0;
}

static const core_file_callbacks source_callbacks = { source_size, source_read, source_close, source_seek };

/* ---- big-endian fields ---- */

static uint32_t be16(const uint8_t *p) { return (uint32_t)p[0] << 8 | p[1]; }
static uint32_t be24(const uint8_t *p) { return (uint32_t)p[0] << 16 | (uint32_t)p[1] << 8 | p[2]; }
static uint32_t be32(const uint8_t *p) { return (uint32_t)p[0] << 24 | be24(p + 1); }
static uint64_t be64(const uint8_t *p) { return (uint64_t)be32(p) << 32 | be32(p + 4); }

static uint64_t hunks_for(uint64_t logical_bytes, uint64_t hunk_bytes)
{
    return logical_bytes / hunk_bytes + (logical_bytes % hunk_bytes != 0);
}

/* ---- checks Moonlark.Libchdr applies before chd_open: ChdHeaderReader.ValidateEnvelope ---- */

static uint32_t expected_header_bytes(uint32_t version)
{
    switch (version)
    {
        case 1: return 76;
        case 2: return 80;
        case 3: return 120;
        case 4: return 108;
        case 5: return 124;
        default: return 0;
    }
}

/* ChdMetadataIndex.Read. A cycle never terminates, so the chain bound also rejects every cycle the
 * managed visited set does. */
static const char *check_metadata_chain(const uint8_t *data, uint64_t size, uint64_t first, uint32_t header_bytes)
{
    uint32_t entries = 0;
    for (uint64_t offset = first; offset != 0; entries++)
    {
        if (entries == MAX_METADATA_CHAIN || offset < header_bytes || offset > size || size - offset < 16)
            return "metadata chain";
        uint32_t payload_bytes = be32(data + offset + 4) & 0x00ffffffu;
        if (payload_bytes > size - (offset + 16)) return "metadata payload size";
        offset = be64(data + offset + 8);
    }
    return NULL;
}

static const char *check_envelope(const uint8_t *data, uint64_t size)
{
    if (size < 16) return "truncated header";
    if (memcmp(data, "MComprHD", 8) != 0) return "header signature";
    uint32_t version = be32(data + 12);
    uint32_t expected = expected_header_bytes(version);
    if (expected == 0) return "header version";
    if (be32(data + 8) != expected || size < expected) return "header length";
    if (version == 5)
    {
        uint64_t logical = be64(data + 32);
        uint32_t hunk = be32(data + 56);
        uint32_t unit = be32(data + 60);
        if (logical == 0 || logical > INT64_MAX || hunk == 0 || hunk > INT32_MAX || unit == 0 ||
            hunks_for(logical, hunk) > UINT32_MAX)
            return "logical/hunk/unit geometry";
        return check_metadata_chain(data, size, be64(data + 48), expected);
    }
    if (version == 3 || version == 4)
    {
        uint64_t logical = be64(data + 28);
        uint32_t hunk = be32(data + (version == 3 ? 76 : 44));
        if (logical == 0 || logical > INT64_MAX || hunk == 0 || hunk > INT32_MAX) return "logical/hunk geometry";
        return check_metadata_chain(data, size, be64(data + 36), expected);
    }
    return NULL;
}

/* ---- harness bounds, applied to the raw header before libchdr allocates from it ---- */

typedef struct {
    uint64_t hunk_bytes;
    uint64_t hunk_count;
} raw_geometry;

/* The hunk size and count an envelope-checked header declares, derived as libchdr's header_read derives them. */
static raw_geometry header_geometry(const uint8_t *data)
{
    uint32_t version = be32(data + 12);
    raw_geometry geometry;
    if (version == 5)
    {
        geometry.hunk_bytes = be32(data + 56);
        geometry.hunk_count = hunks_for(be64(data + 32), geometry.hunk_bytes);
    }
    else if (version >= 3)
    {
        geometry.hunk_bytes = be32(data + (version == 3 ? 76 : 44));
        geometry.hunk_count = be32(data + 24);
    }
    else
    {
        uint64_t sector_bytes = version == 1 ? 512 : be32(data + 76);
        geometry.hunk_bytes = sector_bytes * be32(data + 24);
        geometry.hunk_count = be32(data + 28);
    }
    return geometry;
}

static const char *check_harness_bounds(raw_geometry geometry)
{
    if (geometry.hunk_bytes > MAX_HUNK_BYTES) return "harness bound: hunk bytes";
    if (geometry.hunk_count > MAX_HUNK_COUNT) return "harness bound: hunk count";
    return NULL;
}

/* ---- checks Moonlark.Libchdr applies after chd_open: ChdHeaderReader.Snapshot ---- */

static uint32_t header_hunk_count(const chd_header *header)
{
    return header->version == 5 ? header->hunkcount : header->totalhunks;
}

static const char *check_geometry(const chd_header *header)
{
    uint32_t count = header_hunk_count(header);
    if (header->logicalbytes == 0 || header->logicalbytes > INT64_MAX || header->hunkbytes == 0 ||
        header->hunkbytes > INT32_MAX || header->unitbytes == 0 || count == 0 ||
        hunks_for(header->logicalbytes, header->hunkbytes) != count)
        return "header geometry";
    return NULL;
}

/* ---- ChdMapValidator ---- */

/* One decoded map: libchdr's v5 rawmap, or the fixed-size v1-v4 entries read back from the source. */
typedef struct {
    const uint8_t *entries;
    uint32_t entry_bytes; /* 12 (v5), 16 (v3/v4) or 8 (v1/v2) */
    uint32_t count;
    uint32_t hunk_bytes;
} hunk_map;

static const uint8_t *map_entry(const hunk_map *map, uint32_t index) { return map->entries + (size_t)index * map->entry_bytes; }

static uint32_t legacy_kind(const hunk_map *map, uint32_t index)
{
    const uint8_t *entry = map_entry(map, index);
    if (map->entry_bytes == 16) return entry[15] & 0x0fu;
    return (be64(entry) >> 44) == map->hunk_bytes ? LEGACY_UNCOMPRESSED : LEGACY_COMPRESSED;
}

static bool is_self(const hunk_map *map, uint32_t index)
{
    return map->entry_bytes == V5_ENTRY_BYTES ? map_entry(map, index)[0] == V5_SELF : legacy_kind(map, index) == LEGACY_SELF;
}

/* The self-reference target, data offset or MINI value, by entry kind. */
static uint64_t map_target(const hunk_map *map, uint32_t index)
{
    const uint8_t *entry = map_entry(map, index);
    if (map->entry_bytes == V5_ENTRY_BYTES) return (uint64_t)be16(entry + 4) << 32 | be32(entry + 6);
    return map->entry_bytes == 16 ? be64(entry) : be64(entry) & 0xfffffffffffull;
}

static uint32_t legacy_length(const hunk_map *map, uint32_t index)
{
    const uint8_t *entry = map_entry(map, index);
    return map->entry_bytes == 16 ? (be16(entry + 12) | (uint32_t)entry[14] << 16) : (uint32_t)(be64(entry) >> 44);
}

static const char *resolve_chain(const hunk_map *map, uint32_t index)
{
    uint32_t current = index;
    for (uint32_t depth = 1; depth <= MAX_SELF_REFERENCE_DEPTH; depth++)
    {
        uint64_t target = map_target(map, current);
        if (target >= map->count) return "map: self-reference outside the map";
        current = (uint32_t)target;
        if (!is_self(map, current)) return NULL;
    }
    return "map: self-reference chain too deep";
}

static const char *check_v5_map(const chd_header *header)
{
    for (int slot = 0; slot < 4; slot++)
        if (header->compression[slot] == CODEC_AVHUFF) return "map: A/V Huffman codec";
    if (header->compression[0] == 0) return NULL;
    if (header->mapentrybytes != V5_ENTRY_BYTES || header->rawmap == NULL) return "map: no decoded map";
    hunk_map map = { header->rawmap, V5_ENTRY_BYTES, header->hunkcount, header->hunkbytes };
    for (uint32_t index = 0; index < map.count; index++)
    {
        const uint8_t *entry = map_entry(&map, index);
        uint32_t type = entry[0];
        const char *problem = NULL;
        if (type <= V5_LAST_CODEC_SLOT) problem = header->compression[type] == 0 ? "map: empty codec slot" : NULL;
        else if (type == V5_STORED) problem = be24(entry + 1) != header->hunkbytes ? "map: short stored hunk" : NULL;
        else if (type == V5_SELF) problem = resolve_chain(&map, index);
        else if (type == V5_PARENT) problem = "map: parent entry";
        else problem = "map: unknown entry type";
        if (problem != NULL) return problem;
    }
    return NULL;
}

static const char *check_legacy_entry(const hunk_map *map, uint32_t index, uint32_t codec, uint64_t source_bytes)
{
    switch (legacy_kind(map, index))
    {
        case LEGACY_COMPRESSED:
            if (codec == 0) return "map: compressed entry without a codec";
            /* fall through */
        case LEGACY_UNCOMPRESSED:
        {
            uint64_t offset = map_target(map, index);
            uint32_t length = legacy_length(map, index);
            return length > source_bytes || offset > source_bytes - length ? "map: extent past the source" : NULL;
        }
        case LEGACY_MINI: return map->hunk_bytes < MINI_BYTES ? "map: MINI entry in a hunk under 8 bytes" : NULL;
        case LEGACY_SELF: return resolve_chain(map, index);
        case LEGACY_PARENT: return "map: parent entry";
        default: return "map: unknown entry type";
    }
}

static const char *check_legacy_map(const chd_header *header, const uint8_t *data, uint64_t size)
{
    uint32_t codec = header->compression[0];
    if (codec == LEGACY_AV) return "map: A/V codec";
    if (codec != 0 && codec != LEGACY_ZLIB && codec != LEGACY_ZLIB_PLUS) return "map: not a v1-v4 codec";
    uint32_t entry_bytes = header->version < 3 ? 8 : 16;
    uint64_t map_bytes = (uint64_t)header->totalhunks * entry_bytes;
    if (header->length > size || map_bytes > size - header->length) return "map: past the source";
    hunk_map map = { data + header->length, entry_bytes, header->totalhunks, header->hunkbytes };
    for (uint32_t index = 0; index < map.count; index++)
    {
        const char *problem = check_legacy_entry(&map, index, codec, size);
        if (problem != NULL) return problem;
    }
    return NULL;
}

static const char *check_map(const chd_header *header, const uint8_t *data, uint64_t size)
{
#ifdef CHDR_FUZZ_SKIP_MAP_GUARD
    /* Only for demonstrating the known libchdr defects the map guard exists for; never for fuzzing. */
    (void)header; (void)data; (void)size;
    return NULL;
#else
    return header->version >= 5 ? check_v5_map(header) : check_legacy_map(header, data, size);
#endif
}

/* ---- the walk ---- */

static uint32_t walk_metadata(chd_file *chd)
{
    uint8_t payload[METADATA_BUFFER_BYTES];
    uint32_t index = 0;
    for (; index < MAX_METADATA_ENTRIES; index++)
    {
        uint32_t length = 0, tag = 0;
        uint8_t flags = 0;
        if (chd_get_metadata(chd, CHDMETATAG_WILDCARD, index, payload, sizeof(payload), &length, &tag, &flags) != CHDERR_NONE)
            break;
        /* The tagged lookup takes libchdr's other search path. */
        (void)chd_get_metadata(chd, tag, 0, payload, sizeof(payload), NULL, NULL, NULL);
    }
    return index;
}

typedef struct {
    size_t budget;
    bool reverse;
    uint32_t attempted;
    uint32_t decoded;
} hunk_walk;

/* Reads up to MAX_HUNKS_READ hunks, spread evenly over the image, within MAX_DECODED_BYTES. */
static void read_hunks(chd_file *chd, const chd_header *header, hunk_walk *walk)
{
    uint32_t count = header_hunk_count(header);
    uint32_t reads = count < MAX_HUNKS_READ ? count : MAX_HUNKS_READ;
    uint32_t by_bytes = MAX_DECODED_BYTES / header->hunkbytes;
    if (reads > by_bytes) reads = by_bytes;
    if (reads == 0) return;
    uint8_t *buffer = malloc(header->hunkbytes); /* exactly one hunk, so ASan sees any overrun */
    if (buffer == NULL) return;
    for (uint32_t step = 0; step < reads; step++)
    {
        uint32_t slot = walk->reverse ? reads - 1 - step : step;
        uint32_t hunk = (uint32_t)((uint64_t)slot * count / reads);
        walk->attempted++;
        if (chd_read(chd, hunk, buffer) == CHDERR_NONE) walk->decoded++;
    }
    free(buffer);
}

static bool is_alphanumeric_fourcc(uint32_t codec)
{
    for (int shift = 24; shift >= 0; shift -= 8)
    {
        uint32_t c = codec >> shift & 0xffu;
        if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))) return false;
    }
    return true;
}

/* FourCCs as text when they are alphanumeric, other values as numbers, so the line stays one token per field. */
static void report_codecs(const chd_header *header)
{
    for (int slot = 0; slot < 4; slot++)
    {
        uint32_t codec = header->compression[slot];
        if (is_alphanumeric_fourcc(codec))
            fprintf(stderr, "%s%c%c%c%c", slot ? "," : "", (int)(codec >> 24), (int)(codec >> 16 & 0xff),
                    (int)(codec >> 8 & 0xff), (int)(codec & 0xff));
        else
            fprintf(stderr, "%s%u", slot ? "," : "", codec);
    }
}

static int rejected(const char *stage, size_t size, const char *reason)
{
    if (verbose) fprintf(stderr, "chd_walk: reject stage=%s size=%zu reason=%s\n", stage, size, reason);
    return 0;
}

int LLVMFuzzerInitialize(int *argc, char ***argv)
{
    (void)argc; (void)argv;
    const char *setting = getenv("CHDR_FUZZ_VERBOSE");
    verbose = setting != NULL && strcmp(setting, "1") == 0;
#ifdef CHDR_FUZZ_UBSAN_CANARY
    fprintf(stderr, "chd_walk: canary build: map guard off, UBSan trigger on; never fuzz or replay with it\n");
#endif
    return 0;
}

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size)
{
#ifdef CHDR_FUZZ_UBSAN_CANARY
    if (size >= sizeof(UBSAN_CANARY_TRIGGER) - 1 && memcmp(data, UBSAN_CANARY_TRIGGER, sizeof(UBSAN_CANARY_TRIGGER) - 1) == 0)
    {
        chd_fuzz_ubsan_canary(size);
        return 0;
    }
#endif
    const char *problem = check_envelope(data, size);
    if (problem != NULL) return rejected("envelope", size, problem);
    raw_geometry geometry = header_geometry(data);
    if ((problem = check_harness_bounds(geometry)) != NULL)
    {
        if (verbose)
            fprintf(stderr, "chd_walk: reject stage=bound size=%zu hunkbytes=%" PRIu64 " hunks=%" PRIu64 " reason=%s\n", size,
                    geometry.hunk_bytes, geometry.hunk_count, problem);
        return 0;
    }

    memory_source source = { data, size, 0 };
    chd_file *chd = NULL;
    chd_error error = chd_open_core_file_callbacks(&source_callbacks, &source, CHD_OPEN_READ, NULL, &chd);
    if (error != CHDERR_NONE) return rejected("open", size, chd_error_string(error));

    const chd_header *header = chd_get_header(chd);
    if ((problem = check_geometry(header)) != NULL || (problem = check_map(header, data, size)) != NULL)
    {
        chd_close(chd);
        return rejected("validation", size, problem);
    }

    /* ChdFile.TryOpenCore caps the requested budget at the source length. */
    const size_t budgets[3] = { 0, header->hunkbytes, WHOLE_FILE_READ_AHEAD };
    size_t requested = budgets[size % 3];
    hunk_walk walk = { requested < size ? requested : size, (size / 3) % 2 != 0, 0, 0 };
    (void)chd_set_cache_budget(chd, walk.budget);
    uint32_t metadata = walk_metadata(chd);
    read_hunks(chd, header, &walk);
    if (verbose)
    {
        fprintf(stderr, "chd_walk: accept size=%zu version=%u codecs=", size, header->version);
        report_codecs(header);
        fprintf(stderr, " hunkbytes=%u hunks=%u metadata=%u budget=%zu order=%s read=%u decoded=%u\n", header->hunkbytes,
                header_hunk_count(header), metadata, chd_get_cache_budget(chd), walk.reverse ? "reverse" : "forward",
                walk.attempted, walk.decoded);
    }
    chd_close(chd);
    return 0;
}
