#include "../BoundedSecurityDescriptor.h"

#include <sddl.h>

#include <algorithm>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <vector>

namespace {
using Bytes = std::vector<std::uint8_t>;
unsigned checks = 0;

void Check(bool ok, const char* name) {
    ++checks;
    if (!ok) {
        std::cerr << "FAIL " << name << '\n';
        std::exit(1);
    }
}

Bytes FromSddl(const wchar_t* sddl) {
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    ULONG size = 0;
    Check(ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1, &descriptor, &size) != FALSE,
          "create OS descriptor fixture");
    auto* first = static_cast<std::uint8_t*>(descriptor);
    Bytes result(first, first + size);
    LocalFree(descriptor);
    return result;
}

bool Valid(const Bytes& bytes) {
    return IcemOpc::BoundedSecurityDescriptor(bytes.data(), bytes.size());
}

void Put16(Bytes& bytes, std::size_t offset, std::uint16_t value) {
    bytes[offset] = static_cast<std::uint8_t>(value);
    bytes[offset + 1] = static_cast<std::uint8_t>(value >> 8);
}

void Put32(Bytes& bytes, std::size_t offset, std::uint32_t value) {
    Put16(bytes, offset, static_cast<std::uint16_t>(value));
    Put16(bytes, offset + 2, static_cast<std::uint16_t>(value >> 16));
}

// Windows permits the referenced components in any order. Construct a second
// layout from OS-generated components without changing any SID or ACE bytes.
Bytes Reordered(const Bytes& input) {
    using IcemOpc::DescriptorDetail::U32;
    const std::size_t owner = U32(input.data(), 4), group = U32(input.data(), 8), acl = U32(input.data(), 16);
    const std::size_t ownerLength = 8u + 4u * input.at(owner + 1);
    const std::size_t groupLength = 8u + 4u * input.at(group + 1);
    const std::size_t aclLength = IcemOpc::DescriptorDetail::U16(input.data(), acl + 2);
    Bytes output(sizeof(SECURITY_DESCRIPTOR_RELATIVE) + ownerLength + groupLength + aclLength);
    std::copy_n(input.begin(), sizeof(SECURITY_DESCRIPTOR_RELATIVE), output.begin());
    std::size_t at = sizeof(SECURITY_DESCRIPTOR_RELATIVE);
    Put32(output, 4, static_cast<std::uint32_t>(at));
    std::copy_n(input.begin() + owner, ownerLength, output.begin() + at); at += ownerLength;
    Put32(output, 8, static_cast<std::uint32_t>(at));
    std::copy_n(input.begin() + group, groupLength, output.begin() + at); at += groupLength;
    Put32(output, 16, static_cast<std::uint32_t>(at));
    std::copy_n(input.begin() + acl, aclLength, output.begin() + at);
    return output;
}

void Reject(const Bytes& input, std::size_t offset, std::uint8_t value, const char* name) {
    Bytes changed = input;
    changed.at(offset) = value;
    Check(!Valid(changed), name);
}

// The second page is inaccessible. Each prefix ends exactly at that page, so
// any read past its supplied length raises an access violation in this process.
void GuardPageCases(const std::vector<Bytes>& inputs) {
    SYSTEM_INFO info = {};
    GetSystemInfo(&info);
    const std::size_t page = info.dwPageSize;
    auto* memory = static_cast<std::uint8_t*>(VirtualAlloc(nullptr, page * 2, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    Check(memory != nullptr, "allocate guard pages");
    DWORD oldProtection = 0;
    Check(VirtualProtect(memory + page, page, PAGE_NOACCESS, &oldProtection) != FALSE, "protect guard page");
    for (const auto& input : inputs) {
        Check(input.size() < page, "bounded guard fixture");
        for (std::size_t length = 0; length < input.size(); ++length) {
            auto* start = memory + page - length;
            if (length) std::memcpy(start, input.data(), length);
            Check(!IcemOpc::BoundedSecurityDescriptor(start, length), "truncated descriptor before guard page");
        }
        auto* start = memory + page - input.size();
        std::memcpy(start, input.data(), input.size());
        Check(IcemOpc::BoundedSecurityDescriptor(start, input.size()), "complete descriptor before guard page");
        // Deterministic mutations also exercise bounds at an aligned guard edge.
        std::uint32_t random = 0x6d2b79f5;
        for (unsigned n = 0; n < 4096; ++n) {
            std::memcpy(start, input.data(), input.size());
            random ^= random << 13; random ^= random >> 17; random ^= random << 5;
            const std::size_t index = random % input.size();
            random ^= random << 13; random ^= random >> 17; random ^= random << 5;
            start[index] ^= static_cast<std::uint8_t>((random & 0xffu) | 1u);
            (void)IcemOpc::BoundedSecurityDescriptor(start, input.size());
        }
        ++checks;
    }
    Check(VirtualFree(memory, 0, MEM_RELEASE) != FALSE, "release own guard pages");
}
} // namespace

int main() {
    const Bytes bad = {0x01,0x00,0x04,0x80,0,0,0,0,0,0,0,0,0,0,0,0,
                       0x14,0,0,0, 0x02,0,0,0,0,0,0,0};
    Check(!Valid(bad), "RawSecurityDescriptor counterexample: AclSize=0");
    Check(!IcemOpc::BoundedSecurityDescriptor(nullptr, 0), "null descriptor");

    const std::vector<Bytes> good = {
        FromSddl(L"O:SYG:SYD:(A;;0x3;;;SY)(A;;0x3;;;BA)"),
        FromSddl(L"O:BAG:SYD:P(D;;0xb;;;AN)(A;;0xb;;;SY)(A;;0xb;;;BA)(A;;0xb;;;S-1-5-21-101-202-303-1001)"),
        FromSddl(L"O:SYG:SYD:(A;;0x3;;;AU)S:(AU;SA;0x3;;;WD)"),
        FromSddl(L"O:SYG:SYD:(OA;;0x3;11111111-1111-1111-1111-111111111111;;BA)"),
        FromSddl(L"O:SYG:SY"),
        FromSddl(L"O:SYG:SYD:(OD;;0x3;11111111-1111-1111-1111-111111111111;;BA)"),
        FromSddl(L"O:SYG:SYS:(OU;SA;0x3;11111111-1111-1111-1111-111111111111;;WD)")
    };
    for (const auto& descriptor : good) {
        const Bytes original = descriptor;
        Check(Valid(descriptor), "OS-generated descriptor");
        Check(descriptor == original, "validation must preserve exact bytes");
    }
    Bytes padded = good[0];
    padded.insert(padded.end(), {0x55, 0xaa, 0x7e, 0x31});
    const Bytes paddedOriginal = padded;
    Check(Valid(padded) && padded == paddedOriginal, "unreferenced bytes are not normalized");
    const Bytes reordered = Reordered(good[0]);
    const Bytes reorderedOriginal = reordered;
    Check(reordered != good[0] && Valid(reordered) && reordered == reorderedOriginal, "noncanonical component order is preserved");
    Bytes unaligned(good[0].size() + 1);
    std::copy(good[0].begin(), good[0].end(), unaligned.begin() + 1);
    Check(reinterpret_cast<std::uintptr_t>(unaligned.data() + 1) % alignof(DWORD) != 0 &&
          !IcemOpc::BoundedSecurityDescriptor(unaligned.data() + 1, good[0].size()), "unaligned descriptor refused before Win32");
    Bytes nullDacl = good[4];
    nullDacl[2] |= SE_DACL_PRESENT;
    Check(Valid(nullDacl), "null DACL is structurally valid, not an authorization decision");

    const Bytes& sample = good[0];
    const std::size_t dacl = IcemOpc::DescriptorDetail::U32(sample.data(), 16);
    const std::size_t owner = IcemOpc::DescriptorDetail::U32(sample.data(), 4);
    const std::size_t ace = dacl + sizeof(ACL);
    Reject(sample, 0, 2, "SD revision");
    Reject(sample, 3, 0, "absolute descriptor");
    Reject(sample, 16, 1, "unaligned ACL offset");
    Reject(sample, 16, 4, "ACL offset inside SD header");
    Reject(sample, dacl + 2, 0, "ACL shorter than header");
    Reject(sample, dacl + 4, 0xff, "ACE count exceeds ACL");
    Reject(sample, dacl, 1, "invalid ACL revision");
    Reject(sample, ace + 2, 0, "zero ACE size");
    Reject(sample, ace + 2, 3, "short ACE header");
    Reject(sample, ace + 2, 0xfc, "ACE size exceeds ACL");
    Reject(sample, ace + 2, 17, "unaligned ACE size");
    Reject(sample, ace + 8, 2, "ACE SID revision");
    Reject(sample, ace + 9, 16, "ACE SID subauthority overflow");
    Reject(sample, ace + 9, 15, "ACE SID subauthorities exceed ACE");
    Reject(sample, owner, 2, "owner SID revision");
    Reject(sample, owner + 1, 16, "owner SID subauthority overflow");
    Reject(sample, ace, ACCESS_ALLOWED_CALLBACK_ACE_TYPE, "unsupported callback layout");
    Reject(sample, ace, 0xff, "unknown ACE layout");
    Reject(sample, ace, SYSTEM_AUDIT_ACE_TYPE, "SACL ACE in DACL");
    Bytes changed = sample;
    Put32(changed, 4, 0xfffffffcu);
    Check(!Valid(changed), "overflowing owner offset");
    changed = sample; Put32(changed, 16, 0xfffffffcu);
    Check(!Valid(changed), "overflowing ACL offset");
    changed = sample; Put16(changed, dacl + 2, 0xffff);
    Check(!Valid(changed), "ACL extends past input");
    changed = sample; changed[2] &= static_cast<std::uint8_t>(~SE_DACL_PRESENT);
    Check(!Valid(changed), "DACL offset without present flag");
    changed = sample; Put16(changed, ace + 2, 12);
    Check(!Valid(changed), "ACE SID extends past ACE");
    const Bytes& audit = good[2];
    const std::size_t sacl = IcemOpc::DescriptorDetail::U32(audit.data(), 12);
    Reject(audit, sacl + sizeof(ACL), ACCESS_ALLOWED_ACE_TYPE, "DACL ACE in SACL");
    Reject(audit, sacl + sizeof(ACL), SYSTEM_RESOURCE_ATTRIBUTE_ACE_TYPE, "unsupported resource attribute");
    Reject(audit, sacl + sizeof(ACL), SYSTEM_MANDATORY_LABEL_ACE_TYPE, "unsupported mandatory label");
    changed = audit; Put32(changed, 12, 0xfffffffcu);
    Check(!Valid(changed), "overflowing SACL offset");
    const Bytes& object = good[3];
    const std::size_t objectAcl = IcemOpc::DescriptorDetail::U32(object.data(), 16);
    const std::size_t objectAce = objectAcl + sizeof(ACL);
    Reject(object, objectAcl, ACL_REVISION, "object ACE requires DS ACL revision");
    Reject(object, objectAce + 8, 4, "unknown object flags");
    Reject(object, objectAce + 8, 3, "object GUID overruns ACE SID");
    GuardPageCases(good);
    std::cout << "PASS bounded descriptor checks=" << checks << "; guard-page mutation cases=" << good.size() * 4096 << '\n';
    return 0;
}
