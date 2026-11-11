// Process-local API check only; this does not execute MSI or access the registry.
#include "BoundaryProbe.cpp"

int main() {
    Handle process, thread;
    std::wstring info, access;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE, &process.value) ||
        !TokenDescription(process.value, info) || !ProcessAccess(process.value, access)) return 1;
    if (!ImpersonateSelf(SecurityImpersonation)) return 1;
    bool ok = OpenThreadToken(GetCurrentThread(), TOKEN_QUERY | TOKEN_DUPLICATE, TRUE, &thread.value) &&
              TokenDescription(thread.value, info) && ProcessAccess(thread.value, access);
    if (!RevertToSelf()) return 1;
    return ok ? 0 : 1;
}
