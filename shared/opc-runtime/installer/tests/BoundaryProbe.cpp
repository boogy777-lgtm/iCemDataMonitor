// TEST ONLY: collect execute-side evidence; this is not a preservation guard.
// No registry writes, property updates, privilege changes or descriptor bytes.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <aclapi.h>
#include <msi.h>
#include <msiquery.h>
#include <sddl.h>

#include <sstream>
#include <string>
#include <vector>

namespace {
struct Handle {
    HANDLE value = nullptr;
    ~Handle() { if (value) CloseHandle(value); }
    Handle() = default;
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};

bool Log(MSIHANDLE install, const std::wstring& text) {
    MSIHANDLE record = MsiCreateRecord(1);
    if (!record) return false;
    bool ok = MsiRecordSetStringW(record, 0, L"[1]") == ERROR_SUCCESS &&
              MsiRecordSetStringW(record, 1, text.c_str()) == ERROR_SUCCESS;
    if (ok) MsiProcessMessage(install, INSTALLMESSAGE_INFO, record);
    MsiCloseHandle(record);
    return ok;
}

bool TokenInfo(HANDLE token, TOKEN_INFORMATION_CLASS kind, std::vector<BYTE>& buffer) {
    DWORD bytes = 0;
    if (GetTokenInformation(token, kind, nullptr, 0, &bytes) ||
        GetLastError() != ERROR_INSUFFICIENT_BUFFER || bytes == 0 || bytes > 1024 * 1024) return false;
    buffer.resize(bytes);
    return GetTokenInformation(token, kind, buffer.data(), bytes, &bytes) != FALSE;
}

bool TokenDescription(HANDLE token, std::wstring& result) {
    std::vector<BYTE> user, integrity, elevation, virtualization;
    if (!TokenInfo(token, TokenUser, user) || !TokenInfo(token, TokenIntegrityLevel, integrity) ||
        !TokenInfo(token, TokenElevationType, elevation) ||
        !TokenInfo(token, TokenVirtualizationEnabled, virtualization)) return false;
    PSID sid = reinterpret_cast<TOKEN_USER*>(user.data())->User.Sid;
    PSID level = reinterpret_cast<TOKEN_MANDATORY_LABEL*>(integrity.data())->Label.Sid;
    if (!IsValidSid(sid) || !IsValidSid(level) || *GetSidSubAuthorityCount(level) == 0) return false;
    LPWSTR sidText = nullptr;
    if (!ConvertSidToStringSidW(sid, &sidText)) return false;
    DWORD rid = *GetSidSubAuthority(level, *GetSidSubAuthorityCount(level) - 1);
    std::wostringstream out;
    out << L"sid=" << sidText << L";integrity=" << rid
        << L";elevationType=" << *reinterpret_cast<TOKEN_ELEVATION_TYPE*>(elevation.data())
        << L";virtualization=" << *reinterpret_cast<DWORD*>(virtualization.data());
    LocalFree(sidText);
    result = out.str();
    return true;
}

bool ProcessAccess(HANDLE token, std::wstring& result) {
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    PSID owner = nullptr;
    PSID group = nullptr;
    PACL dacl = nullptr;
    if (GetSecurityInfo(GetCurrentProcess(), SE_KERNEL_OBJECT,
                        OWNER_SECURITY_INFORMATION | GROUP_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
                        &owner, &group, &dacl, nullptr, &descriptor) != ERROR_SUCCESS) return false;
    LPWSTR sddl = nullptr;
    if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(
            descriptor, SDDL_REVISION_1, OWNER_SECURITY_INFORMATION | GROUP_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
            &sddl, nullptr)) { LocalFree(descriptor); return false; }
    Handle impersonation;
    if (!DuplicateToken(token, SecurityImpersonation, &impersonation.value)) {
        LocalFree(sddl); LocalFree(descriptor); return false;
    }
    GENERIC_MAPPING mapping = { STANDARD_RIGHTS_READ | PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
        STANDARD_RIGHTS_WRITE | PROCESS_CREATE_THREAD | PROCESS_VM_OPERATION | PROCESS_VM_WRITE |
            PROCESS_DUP_HANDLE | PROCESS_CREATE_PROCESS | PROCESS_SET_QUOTA | PROCESS_SET_INFORMATION,
        STANDARD_RIGHTS_EXECUTE | SYNCHRONIZE, PROCESS_ALL_ACCESS };
    const DWORD masks[] = { PROCESS_CREATE_THREAD, PROCESS_VM_OPERATION, PROCESS_VM_WRITE,
                            PROCESS_DUP_HANDLE, WRITE_DAC, WRITE_OWNER };
    std::wostringstream out;
    out << L"sddl=" << sddl << L";allowedMask=";
    DWORD allowedMask = 0;
    for (DWORD mask : masks) {
        PRIVILEGE_SET privileges = {};
        DWORD length = sizeof(privileges), granted = 0;
        BOOL allowed = FALSE;
        if (!AccessCheck(descriptor, impersonation.value, mask, &mapping, &privileges,
                         &length, &granted, &allowed)) {
            LocalFree(sddl); LocalFree(descriptor); return false;
        }
        if (allowed) allowedMask |= mask;
    }
    out << allowedMask;
    LocalFree(sddl);
    LocalFree(descriptor);
    result = out.str();
    return true;
}

bool RegistryEvidence(MSIHANDLE install) {
    const wchar_t* paths[] = {
        L"SOFTWARE\\Classes\\AppID\\{13486D44-4821-11D2-A494-3CB306C10000}",
        L"SOFTWARE\\Classes\\AppID\\{CC7D4C09-B26B-45B3-B482-2DECA09B832A}"
    };
    const wchar_t* names[] = { L"AccessPermission", L"LaunchPermission" };
    const REGSAM views[] = { KEY_WOW64_32KEY, KEY_WOW64_64KEY };
    for (unsigned index = 0; index < 2; ++index) {
        for (REGSAM view : views) {
            HKEY key = nullptr;
            LONG opened = RegOpenKeyExW(HKEY_LOCAL_MACHINE, paths[index], 0, KEY_QUERY_VALUE | view, &key);
            for (const wchar_t* name : names) {
                DWORD type = 0, size = 0;
                LONG status = opened == ERROR_SUCCESS ? RegQueryValueExW(key, name, nullptr, &type, nullptr, &size) : opened;
                std::wostringstream out;
                out << L"ICEM_BOUNDARY_REG index=" << index << L";view=" << (view == KEY_WOW64_32KEY ? 32 : 64)
                    << L";name=" << name << L";status=" << status << L";type=" << type << L";size=" << size;
                if (!Log(install, out.str())) { if (key) RegCloseKey(key); return false; }
            }
            if (key) RegCloseKey(key);
        }
    }
    return true;
}
}

extern "C" __declspec(dllexport) UINT __stdcall InspectBoundary(MSIHANDLE install) {
    try {
        Handle process, thread;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE, &process.value)) return ERROR_INSTALL_FAILURE;
        bool impersonated = OpenThreadToken(GetCurrentThread(), TOKEN_QUERY | TOKEN_DUPLICATE, TRUE, &thread.value) != FALSE;
        if (!impersonated && GetLastError() != ERROR_NO_TOKEN) return ERROR_INSTALL_FAILURE;
        std::wstring processInfo, threadInfo, accessInfo;
        if (!TokenDescription(process.value, processInfo) ||
            (impersonated && !TokenDescription(thread.value, threadInfo)) ||
            !ProcessAccess(impersonated ? thread.value : process.value, accessInfo)) return ERROR_INSTALL_FAILURE;
        std::wostringstream header;
        header << L"ICEM_BOUNDARY_CONTEXT pid=" << GetCurrentProcessId() << L";pointerBits=" << sizeof(void*) * 8
               << L";impersonated=" << (impersonated ? 1 : 0);
        wchar_t processPath[32768] = {};
        DWORD processPathLength = static_cast<DWORD>(_countof(processPath));
        HMODULE module = nullptr;
        wchar_t modulePath[32768] = {};
        if (!QueryFullProcessImageNameW(GetCurrentProcess(), 0, processPath, &processPathLength) ||
            !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                reinterpret_cast<LPCWSTR>(&Log), &module) ||
            GetModuleFileNameW(module, modulePath, static_cast<DWORD>(_countof(modulePath))) == 0) return ERROR_INSTALL_FAILURE;
        if (!Log(install, header.str()) || !Log(install, L"ICEM_BOUNDARY_PROCESS " + processInfo) ||
            !Log(install, L"ICEM_BOUNDARY_THREAD " + (impersonated ? threadInfo : L"none")) ||
            !Log(install, L"ICEM_BOUNDARY_ACCESS " + accessInfo) ||
            !Log(install, std::wstring(L"ICEM_BOUNDARY_IMAGE ") + processPath + L";module=" + modulePath)) return ERROR_INSTALL_FAILURE;
        DWORD length = 0;
        wchar_t empty = L'\0';
        UINT status = MsiGetPropertyW(install, L"ICEM_BOUNDARY_PROBE", &empty, &length);
        if (status != ERROR_SUCCESS && status != ERROR_MORE_DATA) return ERROR_INSTALL_FAILURE;
        if (length > 4096) return ERROR_INSTALL_FAILURE;
        std::vector<wchar_t> property(static_cast<size_t>(length) + 1);
        DWORD capacity = length + 1;
        if (MsiGetPropertyW(install, L"ICEM_BOUNDARY_PROBE", property.data(), &capacity) != ERROR_SUCCESS) return ERROR_INSTALL_FAILURE;
        std::wostringstream propertyInfo;
        const bool literalMatch = std::wstring(property.data(), capacity) == L"raw\" OTHER=1;#x00";
        propertyInfo << L"ICEM_BOUNDARY_PROPERTY characters=" << capacity << L";literalMatch=" << (literalMatch ? 1 : 0);
        if (!Log(install, propertyInfo.str()) || !RegistryEvidence(install)) return ERROR_INSTALL_FAILURE;
        return ERROR_SUCCESS;
    } catch (...) {
        return ERROR_INSTALL_FAILURE;
    }
}
