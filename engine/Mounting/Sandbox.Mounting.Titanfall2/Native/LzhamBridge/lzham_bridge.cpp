// Minimal MIT-LZHAM bridge for Titanfall 2 VPK data. This file contains no TFVPK
// or RSX code; it only calls the public LZHAM alpha API with the game's profile.
#include <cstdint>
#include "lzham.h"

extern "C" __declspec(dllexport) int __cdecl tf2_lzham_decompress(
    const std::uint8_t* source,
    std::uint32_t sourceLength,
    std::uint8_t* destination,
    std::uint32_t destinationLength,
    std::uint32_t* written)
{
    if (!source || !destination || !written || !sourceLength || !destinationLength)
        return 0;

    lzham_decompress_params parameters = {};
    parameters.m_struct_size = sizeof(parameters);
    parameters.m_dict_size_log2 = 20; // Titanfall 2 VPK profile
    parameters.m_decompress_flags =
        LZHAM_DECOMP_FLAG_OUTPUT_UNBUFFERED |
        LZHAM_DECOMP_FLAG_COMPUTE_ADLER32 |
        LZHAM_DECOMP_FLAG_COMPUTE_CRC32;

    size_t outputLength = destinationLength;
    lzham_uint32 adler32 = 0;
    lzham_uint32 crc32 = 0;
    const auto status = lzham_decompress_memory(
        &parameters, destination, &outputLength, source, sourceLength, &adler32, &crc32);

    if (status != LZHAM_DECOMP_STATUS_SUCCESS || outputLength > UINT32_MAX)
        return 0;

    *written = static_cast<std::uint32_t>(outputLength);
    return 1;
}
