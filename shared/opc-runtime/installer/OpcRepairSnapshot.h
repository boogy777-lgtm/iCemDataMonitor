#pragma once

#include <windows.h>
#include <msi.h>

#include <array>
#include <cstddef>
#include <string>

namespace IcemOpc {
// Fixed, reviewed machine locations only. writerProperty is a proposed private
// MSI property; neither this table nor the reader proves session/package trust.
struct RepairBinding {
    const wchar_t* key;
    const wchar_t* value;
    const wchar_t* public32;
    const wchar_t* public64;
    const wchar_t* writerProperty;
    REGSAM writerView;
};

// The inventory completeness test checks these against every owned AppID
// security value, including the native component's writer view.
constexpr RepairBinding RepairBindings[] = {
    {L"SOFTWARE\\Classes\\AppID\\{13486D44-4821-11D2-A494-3CB306C10000}", L"AccessPermission",
     L"ICEM_OPC_ENUM_ACCESS32", L"ICEM_OPC_ENUM_ACCESS64", L"IcemOpcEnumAccessHex", KEY_WOW64_32KEY},
    {L"SOFTWARE\\Classes\\AppID\\{13486D44-4821-11D2-A494-3CB306C10000}", L"LaunchPermission",
     L"ICEM_OPC_ENUM_LAUNCH32", L"ICEM_OPC_ENUM_LAUNCH64", L"IcemOpcEnumLaunchHex", KEY_WOW64_32KEY},
    {L"SOFTWARE\\Classes\\AppID\\{CC7D4C09-B26B-45B3-B482-2DECA09B832A}", L"AccessPermission",
     L"ICEM_OPC_CATEGORY_ACCESS32", L"ICEM_OPC_CATEGORY_ACCESS64", L"IcemOpcCategoryAccessHex", KEY_WOW64_64KEY},
    {L"SOFTWARE\\Classes\\AppID\\{CC7D4C09-B26B-45B3-B482-2DECA09B832A}", L"LaunchPermission",
     L"ICEM_OPC_CATEGORY_LAUNCH32", L"ICEM_OPC_CATEGORY_LAUNCH64", L"IcemOpcCategoryLaunchHex", KEY_WOW64_64KEY}
};
constexpr std::size_t RepairBindingCount = sizeof(RepairBindings) / sizeof(RepairBindings[0]);
using RepairSnapshotHex = std::array<std::wstring, RepairBindingCount>;

// Read raw properties and independent fixed HKLM views. On any failure all
// output is empty; on success each element contains hex digits WITHOUT '#x'.
// This is a transport/structure primitive, NOT a trusted MSI guard: do not use
// its result to authorize a write until package, session and host are verified.
bool ReadRepairSnapshot(MSIHANDLE install, RepairSnapshotHex& writerHex);
} // namespace IcemOpc
