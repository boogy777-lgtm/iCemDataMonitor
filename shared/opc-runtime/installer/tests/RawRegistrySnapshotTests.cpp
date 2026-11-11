#include "../RawRegistrySnapshot.h"

#include <cstdint>
#include <string>
#include <vector>

#ifdef _WIN32
#include <windows.h>
static_assert(IcemOpc::BinaryRegistryType == REG_BINARY, "Unexpected Win32 registry type");
#endif

int main() {
    using IcemOpc::BinaryRegistryType;
    using IcemOpc::RawSnapshotMatches;
    std::vector<std::uint8_t> bytes;
    std::wstring raw = L"#x";
    const wchar_t digits[] = L"0123456789abcdef";
    for (unsigned int i = 0; i < 256; ++i) {
        bytes.push_back(static_cast<std::uint8_t>(i));
        raw += digits[i / 16];
        raw += digits[i % 16];
    }
    if (!RawSnapshotMatches(BinaryRegistryType, bytes, raw) ||
        !RawSnapshotMatches(BinaryRegistryType, {0x00, 0xAB, 0xFF}, L"#x00AbfF")) return 1;
    for (std::uint32_t type = 0; type < 256; ++type) {
        if (type != BinaryRegistryType && RawSnapshotMatches(type, bytes, raw)) return 2;
    }
    if (RawSnapshotMatches(BinaryRegistryType, {}, L"#x") ||
        RawSnapshotMatches(BinaryRegistryType, bytes, L"") ||
        RawSnapshotMatches(BinaryRegistryType, bytes, raw.substr(0, raw.size() - 1)) ||
        RawSnapshotMatches(BinaryRegistryType, bytes, raw + L"00") ||
        RawSnapshotMatches(BinaryRegistryType, bytes, raw + L"\" OTHER=1") ||
        RawSnapshotMatches(BinaryRegistryType, bytes, L"##" + raw) ||
        RawSnapshotMatches(BinaryRegistryType, {0}, L"#X00") ||
        RawSnapshotMatches(BinaryRegistryType, {0}, L"#x0g") ||
        RawSnapshotMatches(BinaryRegistryType, {0}, std::wstring(L"#x0\0", 4))) return 3;
    for (std::size_t i = 2; i < raw.size(); ++i) {
        std::wstring changed = raw;
        changed[i] = changed[i] == L'0' ? L'1' : L'0';
        if (RawSnapshotMatches(BinaryRegistryType, bytes, changed)) return 4;
    }
    for (std::size_t i = 0; i < bytes.size(); ++i) {
        auto changedSource = bytes;
        changedSource[i] ^= 1;
        if (RawSnapshotMatches(BinaryRegistryType, changedSource, raw)) return 5;
    }
    return 0;
}
