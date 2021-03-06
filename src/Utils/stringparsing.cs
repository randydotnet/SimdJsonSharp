﻿// This file is a manual port of C code https://github.com/lemire/simdjson to C#
// (c) Daniel Lemire

#define CHECKUNESCAPED
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Runtime.CompilerServices;

#region stdint types and friends
// if you change something here please change it in other files too
using size_t = System.UInt64;
using uint8_t = System.Byte;
using uint64_t = System.UInt64;
using uint32_t = System.UInt32;
using int64_t = System.Int64;
using bytechar = System.SByte;
using unsigned_bytechar = System.Byte;
using uintptr_t = System.UIntPtr;
using static SimdJsonSharp.Utils;
#endregion

namespace SimdJsonSharp
{
    internal static unsafe class stringparsing
    {
        // begin copypasta
        // These chars yield themselves: " \ /
        // b -> backspace, f -> formfeed, n -> newline, r -> cr, t -> horizontal tab
        // u not handled in this table as it's complex
        static readonly uint8_t[] escape_map = new uint8_t[256]
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 0x0.
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0x22, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x2f,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,

            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 0x4.
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x5c, 0, 0, 0, // 0x5.
            0, 0, 0x08, 0, 0, 0, 0x0c, 0, 0, 0, 0, 0, 0, 0, 0x0a, 0, // 0x6.
            0, 0, 0x0d, 0, 0x09, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 0x7.

            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,

            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        };

        // handle a unicode codepoint
        // write appropriate values into dest
        // src will advance 6 bytes or 12 bytes
        // dest will advance a variable amount (return via pointer)
        // return true if the unicode codepoint was valid
        // We work in little-endian then swap at write time
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool handle_unicode_codepoint(uint8_t** src_ptr, uint8_t** dst_ptr)
        {
            uint32_t code_point = hex_to_u32_nocheck(*src_ptr + 2);
            *src_ptr += 6;
            // check for low surrogate for characters outside the Basic
            // Multilingual Plane.
            if (code_point >= 0xd800 && code_point < 0xdc00)
            {
                if (((*src_ptr)[0] != (bytechar) '\\') || (*src_ptr)[1] != (bytechar) 'u')
                {
                    return false;
                }

                uint32_t code_point_2 = hex_to_u32_nocheck(*src_ptr + 2);
                code_point =
                    (((code_point - 0xd800) << 10) | (code_point_2 - 0xdc00)) + 0x10000;
                *src_ptr += 6;
            }

            size_t offset = codepoint_to_utf8(code_point, *dst_ptr);
            *dst_ptr += offset;
            return offset > 0;
        }

        public static bool parse_string(uint8_t* buf, size_t len, ParsedJson pj, uint32_t depth, uint32_t offset)
        {
#if SIMDJSON_SKIPSTRINGPARSING // for performance analysis, it is sometimes useful to skip parsing
            pj.write_tape(0, '"');// don't bother with the string parsing at all
            return true; // always succeeds
#else
            uint8_t* src = &buf[offset + 1]; // we know that buf at offset is a "
            uint8_t* dst = pj.current_string_buf_loc;
#if JSON_TEST_STRINGS // for unit testing
            uint8_t *const start_of_string = dst;
#endif
            var slashVec = Vector256.Create((byte) '\\');
            var quoteVec = Vector256.Create((byte) '"');

            while (true)
            {
                Vector256<byte> v = Avx2.LoadVector256((src));
                uint32_t bs_bits =
                    (uint32_t) Avx2.MoveMask(Avx2.CompareEqual(v, slashVec));
                uint32_t quote_bits =
                    (uint32_t) Avx2.MoveMask(Avx2.CompareEqual(v, quoteVec));
                // All Unicode characters may be placed within the
                // quotation marks, except for the characters that MUST be escaped:
                // quotation mark, reverse solidus, and the control characters (U+0000
                //through U+001F).
                // https://tools.ietf.org/html/rfc8259
#if CHECKUNESCAPED
                var unitsep = Vector256.Create((byte) 0x1F);
                var unescaped_vec =
                    Avx2.CompareEqual(Avx2.Max(unitsep, v), unitsep); // could do it with saturated subtraction
#endif // CHECKUNESCAPED

                uint32_t quote_dist = (uint32_t) trailingzeroes(quote_bits);
                uint32_t bs_dist = (uint32_t) trailingzeroes(bs_bits);
                // store to dest unconditionally - we can overwrite the bits we don't like
                // later
                Avx.Store((dst), v);
                if (quote_dist < bs_dist)
                {
                    // we encountered quotes first. Move dst to point to quotes and exit
                    dst[quote_dist] = 0; // null terminate and get out

                    pj.WriteTape((size_t) pj.current_string_buf_loc - (size_t) pj.string_buf, (uint8_t) '"');

                    pj.current_string_buf_loc = dst + quote_dist + 1; // the +1 is due to the 0 value
#if CHECKUNESCAPED
                    // check that there is no unescaped char before the quote
                    uint32_t unescaped_bits = (uint32_t) Avx2.MoveMask(unescaped_vec);
                    bool is_ok = ((quote_bits - 1) & (~quote_bits) & unescaped_bits) == 0;
#if JSON_TEST_STRINGS // for unit testing
                    if (is_ok) foundString(buf + offset, start_of_string, pj.current_string_buf_loc - 1);
                    else foundBadString(buf + offset);
#endif // JSON_TEST_STRINGS
                    return is_ok;
#else //CHECKUNESCAPED
#if JSON_TEST_STRINGS // for unit testing
                    foundString(buf + offset, start_of_string, pj.current_string_buf_loc - 1);
#endif // JSON_TEST_STRINGS
                    return true;
#endif //CHECKUNESCAPED
                }
                else if (quote_dist > bs_dist)
                {
                    uint8_t escape_char = src[bs_dist + 1];
#if CHECKUNESCAPED
                    // we are going to need the unescaped_bits to check for unescaped chars
                    uint32_t unescaped_bits = (uint32_t) Avx2.MoveMask(unescaped_vec);
                    if (((bs_bits - 1) & (~bs_bits) & unescaped_bits) != 0)
                    {
#if JSON_TEST_STRINGS // for unit testing
                        foundBadString(buf + offset);
#endif // JSON_TEST_STRINGS
                        return false;
                    }
#endif //CHECKUNESCAPED
                    // we encountered backslash first. Handle backslash
                    if (escape_char == 'u')
                    {
                        // move src/dst up to the start; they will be further adjusted
                        // within the unicode codepoint handling code.
                        src += bs_dist;
                        dst += bs_dist;
                        if (!handle_unicode_codepoint(&src, &dst))
                        {
#if JSON_TEST_STRINGS // for unit testing
                            foundBadString(buf + offset);
#endif // JSON_TEST_STRINGS
                            return false;
                        }
                    }
                    else
                    {
                        // simple 1:1 conversion. Will eat bs_dist+2 characters in input and
                        // write bs_dist+1 characters to output
                        // note this may reach beyond the part of the buffer we've actually
                        // seen. I think this is ok
                        uint8_t escape_result = escape_map[escape_char];
                        if (escape_result == 0)
                        {
#if JSON_TEST_STRINGS // for unit testing
                            foundBadString(buf + offset);
#endif // JSON_TEST_STRINGS
                            return false; // bogus escape value is an error
                        }

                        dst[bs_dist] = escape_result;
                        src += bs_dist + 2;
                        dst += bs_dist + 1;
                    }
                }
                else
                {
                    // they are the same. Since they can't co-occur, it means we encountered
                    // neither.
                    src += 32;
                    dst += 32;
#if CHECKUNESCAPED
                    // check for unescaped chars
                    if (Avx.TestZ(unescaped_vec, unescaped_vec) != true)
                    {
#if JSON_TEST_STRINGS // for unit testing
                        foundBadString(buf + offset);
#endif // JSON_TEST_STRINGS
                        return false;
                    }
#endif // CHECKUNESCAPED
                }
            }

            // can't be reached
            return true;
#endif // SIMDJSON_SKIPSTRINGPARSING
        }
    }
}
