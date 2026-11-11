// TEST ONLY: inserted exclusively in the isolated, non-distributable MSI copy.
// It intentionally does not establish package/session/process trust. Never ship.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <msi.h>
#include <msiquery.h>

#include "../OpcRepairSnapshot.h"

#include <string>

namespace {
bool Log(MSIHANDLE install, const wchar_t* text) {
    MSIHANDLE record = MsiCreateRecord(1);
    if (!record) return false;
    const bool ok = MsiRecordSetStringW(record, 0, L"[1]") == ERROR_SUCCESS &&
                    MsiRecordSetStringW(record, 1, text) == ERROR_SUCCESS;
    if (ok) MsiProcessMessage(install, INSTALLMESSAGE_INFO, record);
    MsiCloseHandle(record);
    return ok;
}
} // namespace

extern "C" __declspec(dllexport) UINT __stdcall TestOnlyRepairSnapshot(MSIHANDLE install) {
    try {
        IcemOpc::RepairSnapshotHex values;
        if (!IcemOpc::ReadRepairSnapshot(install, values)) {
            Log(install, L"ICEM_TEST_SNAPSHOT_REJECTED");
            return ERROR_INSTALL_FAILURE;
        }
        for (std::size_t i = 0; i < IcemOpc::RepairBindingCount; ++i) {
            if (MsiSetPropertyW(install, IcemOpc::RepairBindings[i].writerProperty, values[i].c_str()) != ERROR_SUCCESS)
                return ERROR_INSTALL_FAILURE;
        }
        return Log(install, L"ICEM_TEST_SNAPSHOT_VALIDATED") ? ERROR_SUCCESS : ERROR_INSTALL_FAILURE;
    } catch (...) {
        return ERROR_INSTALL_FAILURE;
    }
}

// Models a public property changed after AppSearch, so the real reader, rather
// than AppSearch's usual overwrite, must reject it. Only the private CI copy
// schedules this action and only when its explicit test property is set.
extern "C" __declspec(dllexport) UINT __stdcall TestOnlyTamperSnapshot(MSIHANDLE install) {
    wchar_t mode[32] = {};
    DWORD length = _countof(mode);
    if (MsiGetPropertyW(install, L"ICEM_TEST_TAMPER", mode, &length) != ERROR_SUCCESS) return ERROR_INSTALL_FAILURE;
    const std::wstring input(mode, length);
    const wchar_t* replacement = nullptr;
    if (input == L"missing") replacement = L"";
    else if (input == L"tamper") replacement = L"#x00";
    else if (input == L"odd") replacement = L"#x123";
    else if (input == L"injection") replacement = L"#x00\" OTHER=1";
    else return ERROR_INSTALL_FAILURE;
    if (MsiSetPropertyW(install, IcemOpc::RepairBindings[0].public32, replacement) != ERROR_SUCCESS ||
        !Log(install, L"ICEM_TEST_SNAPSHOT_TAMPERED")) return ERROR_INSTALL_FAILURE;
    return ERROR_SUCCESS;
}
