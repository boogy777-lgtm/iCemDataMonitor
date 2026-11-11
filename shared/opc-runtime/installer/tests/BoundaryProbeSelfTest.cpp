// Process-local API check only; this does not execute MSI or access the registry.
#include "BoundaryProbe.cpp"

int main() {
    Handle process, thread;
    std::wstring info, access;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE, &process.value) ||
        !TokenDescription(process.value, info) || !ProcessAccess(process.value, access) ||
        !RestrictedProcessAccess(process.value, nullptr, access)) return 1;
    Handle unexpected;
    if (OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &unexpected.value) || GetLastError() != ERROR_NO_TOKEN) return 1;
    if (!ImpersonateSelf(SecurityImpersonation)) return 1;
    bool ok = OpenThreadToken(GetCurrentThread(), TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_IMPERSONATE, TRUE, &thread.value) &&
              TokenDescription(thread.value, info) && ProcessAccess(thread.value, access) &&
              RestrictedProcessAccess(thread.value, thread.value, access);
    Handle restored;
    TOKEN_STATISTICS before = {}, after = {};
    DWORD bytes = 0;
    ok = ok && OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &restored.value) &&
         GetTokenInformation(thread.value, TokenStatistics, &before, sizeof(before), &bytes) &&
         GetTokenInformation(restored.value, TokenStatistics, &after, sizeof(after), &bytes) &&
         before.TokenId.LowPart == after.TokenId.LowPart && before.TokenId.HighPart == after.TokenId.HighPart;
    if (!RevertToSelf()) return 1;
    return ok ? 0 : 1;
}
