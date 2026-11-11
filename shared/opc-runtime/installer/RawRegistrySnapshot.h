#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace IcemOpc {
// REG_BINARY from the Win32 registry API. Other registry types are not snapshots.
constexpr std::uint32_t BinaryRegistryType = 3;

inline unsigned int HexDigit(wchar_t value) noexcept {
    if (value >= L'0' && value <= L'9') return static_cast<unsigned int>(value - L'0');
    if (value >= L'a' && value <= L'f') return static_cast<unsigned int>(value - L'a') + 10;
    if (value >= L'A' && value <= L'F') return static_cast<unsigned int>(value - L'A') + 10;
    return 16;
}

// Compare the raw, unformatted MSI property to independently read registry bytes.
// This is ONLY transport validation: callers must separately validate the
// security descriptor and prove source/process provenance and race safety.
inline bool RawSnapshotMatches(std::uint32_t registryType, const std::vector<std::uint8_t>& source,
                               const std::wstring& property) noexcept {
    if (registryType != BinaryRegistryType || source.empty() || property.size() < 2 ||
        property[0] != L'#' || property[1] != L'x' || (property.size() - 2) % 2 != 0 ||
        (property.size() - 2) / 2 != source.size()) return false;
    for (std::size_t i = 0; i < source.size(); ++i) {
        unsigned int high = HexDigit(property[2 + 2 * i]);
        unsigned int low = HexDigit(property[3 + 2 * i]);
        if (high > 15 || low > 15 || (high * 16 + low) != source[i]) return false;
    }
    return true;
}
}
