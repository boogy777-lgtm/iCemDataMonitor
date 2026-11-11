// Compile the real reader with process-local Win32/MSI fakes: no registry or MSI
// session is opened. The production translation unit is compiled separately.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <msi.h>
#include <msiquery.h>
#include <sddl.h>

#include "../OpcRepairSnapshot.h"

#include <algorithm>
#include <array>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <map>
#include <string>
#include <vector>

static LSTATUS WINAPI FakeRegOpenKeyExW(HKEY, LPCWSTR, DWORD, REGSAM, PHKEY);
static LSTATUS WINAPI FakeRegQueryValueExW(HKEY, LPCWSTR, LPDWORD, LPDWORD, LPBYTE, LPDWORD);
static LSTATUS WINAPI FakeRegCloseKey(HKEY);
static UINT WINAPI FakeMsiGetPropertyW(MSIHANDLE, LPCWSTR, LPWSTR, LPDWORD);
#define RegOpenKeyExW FakeRegOpenKeyExW
#define RegQueryValueExW FakeRegQueryValueExW
#define RegCloseKey FakeRegCloseKey
#define MsiGetPropertyW FakeMsiGetPropertyW
#include "../OpcRepairSnapshot.cpp"
#undef RegOpenKeyExW
#undef RegQueryValueExW
#undef RegCloseKey
#undef MsiGetPropertyW

namespace {
using Bytes = std::vector<std::uint8_t>;
struct Cell {
    Bytes bytes;
    DWORD type = REG_BINARY;
    bool missing = false;
    bool failData = false;
    bool growOnRead = false;
    bool changeType = false;
    bool changeSize = false;
    bool oversized = false;
};
struct Handle { const wchar_t* key = nullptr; unsigned view = 0; };
std::array<std::array<Cell, 2>, IcemOpc::RepairBindingCount> cells;
std::array<Handle, IcemOpc::RepairBindingCount * 2> handles;
std::map<std::wstring, std::wstring> properties;
std::wstring failProperty;
bool failOpen = false;
unsigned opens = 0, closes = 0, checks = 0;
bool apiViolation = false;

void Check(bool condition, const char* message) {
    ++checks;
    if (!condition) { std::cerr << "FAIL " << message << '\n'; std::exit(1); }
}
Bytes FromSddl(const wchar_t* text) {
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    ULONG length = 0;
    Check(ConvertStringSecurityDescriptorToSecurityDescriptorW(text, SDDL_REVISION_1, &descriptor, &length) != FALSE,
          "create OS security descriptor");
    auto* first = static_cast<std::uint8_t*>(descriptor);
    Bytes bytes(first, first + length);
    LocalFree(descriptor);
    return bytes;
}
std::wstring Raw(const Bytes& bytes) {
    constexpr wchar_t digits[] = L"0123456789ABCDEF";
    std::wstring value = L"#x";
    for (std::uint8_t byte : bytes) {
        value += digits[byte >> 4];
        value += digits[byte & 15u];
    }
    return value;
}
const wchar_t* Property(std::size_t binding, unsigned view) {
    return view == 0 ? IcemOpc::RepairBindings[binding].public32 : IcemOpc::RepairBindings[binding].public64;
}
void Reset() {
    const Bytes valid = FromSddl(L"O:SYG:SYD:(D;;0x3;;;AN)(A;;0xb;;;SY)(A;;0xb;;;BA)");
    properties.clear();
    for (std::size_t i = 0; i < cells.size(); ++i) {
        for (unsigned view = 0; view < 2; ++view) {
            cells[i][view] = {};
            cells[i][view].bytes = valid;
            // Distinct trailing bytes ensure that every binding/view and the
            // selected writer view is checked without normalizing the SD.
            cells[i][view].bytes.insert(cells[i][view].bytes.end(),
                                        {0x90, static_cast<std::uint8_t>(i), static_cast<std::uint8_t>(view), 0x7e});
            properties[Property(i, view)] = Raw(cells[i][view].bytes);
        }
    }
    failProperty.clear(); failOpen = false; opens = 0; closes = 0; apiViolation = false;
}
void Expect(bool accepted, const char* name) {
    IcemOpc::RepairSnapshotHex result;
    result.fill(L"preexisting data must never survive a failed read");
    Check(IcemOpc::ReadRepairSnapshot(123, result) == accepted, name);
    Check(!apiViolation && opens == closes, "fixed HKLM API arguments and balanced handles");
    for (std::size_t i = 0; i < result.size(); ++i) {
        const unsigned view = IcemOpc::RepairBindings[i].writerView == KEY_WOW64_32KEY ? 0u : 1u;
        Check(accepted ? result[i] == Raw(cells[i][view].bytes).substr(2) : result[i].empty(),
              "all-or-nothing writer hex without #x");
    }
}
} // namespace

static LSTATUS WINAPI FakeRegOpenKeyExW(HKEY root, LPCWSTR key, DWORD options, REGSAM access, PHKEY result) {
    const REGSAM view = access & (KEY_WOW64_32KEY | KEY_WOW64_64KEY);
    if (root != HKEY_LOCAL_MACHINE || options != 0 || access != (KEY_QUERY_VALUE | view) ||
        (view != KEY_WOW64_32KEY && view != KEY_WOW64_64KEY)) { apiViolation = true; return ERROR_ACCESS_DENIED; }
    if (failOpen) return ERROR_ACCESS_DENIED;
    for (std::size_t i = 0; i < IcemOpc::RepairBindingCount; ++i) {
        if (std::wstring(key) == IcemOpc::RepairBindings[i].key) {
            const unsigned v = view == KEY_WOW64_32KEY ? 0u : 1u;
            Handle& handle = handles[i * 2 + v];
            handle = {key, v};
            *result = reinterpret_cast<HKEY>(&handle);
            ++opens;
            return ERROR_SUCCESS;
        }
    }
    apiViolation = true;
    return ERROR_FILE_NOT_FOUND;
}
static LSTATUS WINAPI FakeRegQueryValueExW(HKEY key, LPCWSTR value, LPDWORD reserved, LPDWORD type,
                                           LPBYTE data, LPDWORD length) {
    auto* handle = reinterpret_cast<Handle*>(key);
    if (reserved || !type || !length) { apiViolation = true; return ERROR_INVALID_PARAMETER; }
    for (std::size_t i = 0; i < IcemOpc::RepairBindingCount; ++i) {
        const auto& binding = IcemOpc::RepairBindings[i];
        if (std::wstring(handle->key) != binding.key || std::wstring(value) != binding.value) continue;
        const Cell& cell = cells[i][handle->view];
        if (cell.missing) return ERROR_FILE_NOT_FOUND;
        *type = data && cell.changeType ? REG_SZ : cell.type;
        const DWORD size = cell.oversized ? 256u * 1024u + 1u : static_cast<DWORD>(cell.bytes.size());
        if (!data) { *length = size; return ERROR_SUCCESS; }
        if (cell.failData) return ERROR_ACCESS_DENIED;
        if (cell.growOnRead) { *length = size + 1; return ERROR_MORE_DATA; }
        if (*length < size) { *length = size; return ERROR_MORE_DATA; }
        std::memcpy(data, cell.bytes.data(), size);
        *length = cell.changeSize ? size - 1 : size;
        return ERROR_SUCCESS;
    }
    apiViolation = true;
    return ERROR_FILE_NOT_FOUND;
}
static LSTATUS WINAPI FakeRegCloseKey(HKEY) { ++closes; return ERROR_SUCCESS; }
static UINT WINAPI FakeMsiGetPropertyW(MSIHANDLE install, LPCWSTR name, LPWSTR buffer, LPDWORD capacity) {
    if (install != 123 || !name || !buffer || !capacity) { apiViolation = true; return ERROR_INVALID_PARAMETER; }
    if (failProperty == name) return ERROR_INSTALL_FAILURE;
    const auto it = properties.find(name);
    const std::wstring value = it == properties.end() ? L"" : it->second;
    if (*capacity <= value.size()) { *capacity = static_cast<DWORD>(value.size()); return ERROR_MORE_DATA; }
    std::copy(value.begin(), value.end(), buffer);
    buffer[value.size()] = L'\0';
    *capacity = static_cast<DWORD>(value.size());
    return ERROR_SUCCESS;
}

int main(int argc, char** argv) {
    if (argc == 2 && std::strcmp(argv[1], "--manifest") == 0) {
        for (const auto& binding : IcemOpc::RepairBindings) {
            if (binding.writerView != KEY_WOW64_32KEY && binding.writerView != KEY_WOW64_64KEY) return 1;
            std::wcout << (binding.writerView == KEY_WOW64_32KEY ? L"x86" : L"x64") << L'|'
                       << binding.key << L'|' << binding.value << L'|' << binding.public32 << L'|'
                       << binding.public64 << L'|' << binding.writerProperty << L'\n';
        }
        return 0;
    }
    Check(argc == 1, "no unexpected arguments");
    Reset(); Expect(true, "distinct valid machine values and raw properties");
    Reset(); failOpen = true; Expect(false, "machine key inaccessible");
    const Bytes malformed = {0x01,0,0x04,0x80,0,0,0,0,0,0,0,0,0,0,0,0,
                             0x14,0,0,0,0x02,0,0,0,0,0,0,0};
    for (std::size_t i = 0; i < cells.size(); ++i) {
        for (unsigned view = 0; view < 2; ++view) {
            Reset(); cells[i][view].missing = true; Expect(false, "missing machine value");
            Reset(); cells[i][view].type = REG_SZ; Expect(false, "non-binary machine value");
            Reset(); cells[i][view].type = REG_DWORD; Expect(false, "DWORD machine value");
            Reset(); cells[i][view].bytes.clear(); Expect(false, "empty binary machine value");
            Reset(); cells[i][view].oversized = true; Expect(false, "oversized machine value");
            Reset(); cells[i][view].failData = true; Expect(false, "machine data read error");
            Reset(); cells[i][view].growOnRead = true; Expect(false, "machine value grew during read");
            Reset(); cells[i][view].changeType = true; Expect(false, "machine type changed during read");
            Reset(); cells[i][view].changeSize = true; Expect(false, "machine size changed during read");
            Reset(); cells[i][view].bytes = malformed; properties[Property(i, view)] = Raw(malformed);
            Expect(false, "matching malformed descriptor");
            Reset(); cells[i][view].bytes[0] = 2; properties[Property(i, view)] = Raw(cells[i][view].bytes);
            Expect(false, "matching malformed revision");
            Reset(); properties.erase(Property(i, view)); Expect(false, "missing property");
            Reset(); properties[Property(i, view)].clear(); Expect(false, "empty property");
            Reset(); properties[Property(i, view)][2] = L'F'; Expect(false, "property tampering");
            Reset(); properties[Property(i, view)][1] = L'X'; Expect(false, "incorrect raw prefix");
            Reset(); properties[Property(i, view)] += L"\" OTHER=1"; Expect(false, "property injection");
            Reset(); properties[Property(i, view)][5] = L'"'; Expect(false, "same-length quoted property");
            Reset(); properties[Property(i, view)][5] = L'\0'; Expect(false, "embedded null property");
            Reset(); properties[Property(i, view)][2] = L'Z'; Expect(false, "non-hex property");
            Reset(); properties[Property(i, view)].pop_back(); Expect(false, "short property");
            Reset(); properties[Property(i, view)].push_back(L'0'); Expect(false, "overlong property");
            Reset(); failProperty = Property(i, view); Expect(false, "MSI property API failure");
            Reset(); cells[i][view].bytes.back() ^= 1; Expect(false, "snapshot changed after property capture");
        }
    }
    Reset();
    for (auto& item : properties) {
        std::transform(item.second.begin() + 2, item.second.end(), item.second.begin() + 2,
                       [](wchar_t c) { return c >= L'A' && c <= L'F' ? static_cast<wchar_t>(c + 32) : c; });
    }
    // Success still preserves bytes even if the raw hex representation is lower-case.
    IcemOpc::RepairSnapshotHex lower;
    Check(IcemOpc::ReadRepairSnapshot(123, lower) && opens == closes && !apiViolation, "lowercase raw hex");
    for (std::size_t i = 0; i < lower.size(); ++i) {
        const unsigned v = IcemOpc::RepairBindings[i].writerView == KEY_WOW64_32KEY ? 0u : 1u;
        Check(IcemOpc::RawSnapshotMatches(REG_BINARY, cells[i][v].bytes, L"#x" + lower[i]), "lowercase exact bytes");
    }
    std::cout << "PASS fixed repair snapshot checks=" << checks << "; mocked APIs, no MSI or registry effects\n";
    return 0;
}
