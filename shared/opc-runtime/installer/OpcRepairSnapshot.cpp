#include "OpcRepairSnapshot.h"
#include "BoundedSecurityDescriptor.h"
#include "RawRegistrySnapshot.h"

#include <msiquery.h>

#include <cstdint>
#include <utility>
#include <vector>

namespace IcemOpc {
static_assert(BinaryRegistryType == REG_BINARY, "Unexpected Win32 registry type");
namespace {
// More than two maximum-size ACLs, two maximum-size SIDs and the relative
// header. Extra padding is retained, but values above 256 KiB are refused as a
// resource bound; this is not a Windows SD size limit or a policy decision.
constexpr DWORD MaxDescriptorBytes = 256u * 1024u;
struct RegistryKey {
    HKEY handle = nullptr;
    ~RegistryKey() { if (handle) RegCloseKey(handle); }
};

bool ReadMachineValue(const RepairBinding& binding, REGSAM view, std::vector<std::uint8_t>& bytes) {
    RegistryKey key;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, binding.key, 0, KEY_QUERY_VALUE | view, &key.handle) != ERROR_SUCCESS) return false;
    DWORD type = 0, length = 0;
    if (RegQueryValueExW(key.handle, binding.value, nullptr, &type, nullptr, &length) != ERROR_SUCCESS ||
        type != REG_BINARY || length == 0 || length > MaxDescriptorBytes) return false;
    bytes.resize(length);
    DWORD actual = length;
    type = 0;
    if (RegQueryValueExW(key.handle, binding.value, nullptr, &type, bytes.data(), &actual) != ERROR_SUCCESS ||
        type != REG_BINARY || actual != length) return false;
    return BoundedSecurityDescriptor(bytes.data(), bytes.size());
}

bool ReadMatchingProperty(MSIHANDLE install, const wchar_t* name, const std::vector<std::uint8_t>& bytes,
                          std::wstring& hex) {
    // Size the read from the already bounded machine value, never the
    // untrusted property. An overlong/changed property yields MORE_DATA.
    const DWORD length = static_cast<DWORD>(2u + 2u * bytes.size());
    std::vector<wchar_t> buffer(static_cast<std::size_t>(length) + 1u);
    DWORD capacity = length + 1u;
    if (MsiGetPropertyW(install, name, buffer.data(), &capacity) != ERROR_SUCCESS || capacity != length) return false;
    const std::wstring raw(buffer.data(), capacity);
    if (!RawSnapshotMatches(REG_BINARY, bytes, raw)) return false;
    // Proposed input for a Registry row of '#x[private]': only validated hex
    // digits, never another '#x'. Verify actual compiled tables and MSI effect
    // before adopting this representation in an installer.
    hex.assign(raw, 2, std::wstring::npos);
    return true;
}
} // namespace

bool ReadRepairSnapshot(MSIHANDLE install, RepairSnapshotHex& writerHex) {
    writerHex = {};
    RepairSnapshotHex staged;
    for (std::size_t i = 0; i < RepairBindingCount; ++i) {
        const auto& binding = RepairBindings[i];
        const REGSAM views[] = {KEY_WOW64_32KEY, KEY_WOW64_64KEY};
        for (REGSAM view : views) {
            std::vector<std::uint8_t> bytes;
            std::wstring hex;
            const wchar_t* property = view == KEY_WOW64_32KEY ? binding.public32 : binding.public64;
            if (!ReadMachineValue(binding, view, bytes) || !ReadMatchingProperty(install, property, bytes, hex)) return false;
            if (view == binding.writerView) staged[i] = std::move(hex);
        }
        if (staged[i].empty()) return false;
    }
    writerHex = std::move(staged);
    return true;
}
} // namespace IcemOpc
