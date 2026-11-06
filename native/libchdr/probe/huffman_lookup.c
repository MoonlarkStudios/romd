#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "libchdr/huffman.h"

/* Incomplete trees must decode identically regardless of allocator contents or
 * a previously imported tree. This probe is run by every native library build. */
static int check_width(int width)
{
    const unsigned entries = 1u << width;
    for (int pattern = 0; pattern <= 255; pattern += 255)
    {
        struct huffman_decoder *decoder = create_huffman_decoder(2, width);
        if (decoder == NULL) return 1;
        memset(decoder->huffnode, 0, 2 * sizeof(*decoder->huffnode));
        memset(decoder->lookup, pattern, entries * sizeof(*decoder->lookup));
        decoder->huffnode[0].numbits = 1;
        if (huffman_assign_canonical_codes(decoder) != HUFFERR_NONE ||
            huffman_build_lookup_table(decoder) != HUFFERR_NONE) return 1;
        unsigned char input[] = { 0xff, 0xff, 0xff, 0xff };
        struct bitstream *bits = create_bitstream(input, sizeof(input));
        if (bits == NULL) return 1;
        unsigned symbol = huffman_decode_one(decoder, bits);
        int consumed = (int)bits->doffset * 8 - bits->bits;
        free(bits);
        delete_huffman_decoder(decoder);
        if (symbol != 0 || consumed != 1)
        {
            fprintf(stderr, "Huffman lookup depends on prefill: width=%d prefill=%d symbol=%u bits=%d\n",
                width, pattern, symbol, consumed);
            return 1;
        }
    }
    struct huffman_decoder *decoder = create_huffman_decoder(2, width);
    if (decoder == NULL) return 1;
    memset(decoder->huffnode, 0, 2 * sizeof(*decoder->huffnode));
    decoder->huffnode[0].numbits = decoder->huffnode[1].numbits = 1;
    if (huffman_assign_canonical_codes(decoder) != HUFFERR_NONE ||
        huffman_build_lookup_table(decoder) != HUFFERR_NONE) return 1;
    decoder->huffnode[1].numbits = 0;
    if (huffman_assign_canonical_codes(decoder) != HUFFERR_NONE ||
        huffman_build_lookup_table(decoder) != HUFFERR_NONE) return 1;
    for (unsigned index = entries / 2; index < entries; index++)
        if (decoder->lookup[index] != 1)
        {
            fprintf(stderr, "Huffman lookup retained a previous tree: width=%d entry=%u\n", width, index);
            delete_huffman_decoder(decoder);
            return 1;
        }
    delete_huffman_decoder(decoder);
    return 0;
}

int main(void)
{
    if (check_width(1) || check_width(8) || check_width(16)) return 1;
    puts("Huffman lookup initialization passed (allocator patterns and reused trees, widths 1/8/16)");
    return 0;
}
