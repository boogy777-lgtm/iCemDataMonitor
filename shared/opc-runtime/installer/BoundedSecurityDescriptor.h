#pragma once

#include <windows.h>

#include <cstddef>
#include <cstdint>

namespace IcemOpc {
namespace DescriptorDetail {
// All offsets are checked with subtraction before dereferencing or adding.
inline bool Fits(std::size_t size, std::size_t offset, std::size_t length) noexcept {
    return offset <= size && length <= size - offset;
}

inline std::uint16_t U16(const std::uint8_t* data, std::size_t offset) noexcept {
    return static_cast<std::uint16_t>(data[offset] | (static_cast<unsigned>(data[offset + 1]) << 8));
}

inline std::uint32_t U32(const std::uint8_t* data, std::size_t offset) noexcept {
    return static_cast<std::uint32_t>(U16(data, offset)) |
           (static_cast<std::uint32_t>(U16(data, offset + 2)) << 16);
}

inline bool Sid(const std::uint8_t* data, std::size_t end, std::size_t offset) noexcept {
    constexpr std::size_t header = 8; // revision, subauthority count, identifier authority
    if (!Fits(end, offset, header) || data[offset] != SID_REVISION ||
        data[offset + 1] > SID_MAX_SUB_AUTHORITIES) return false;
    const std::size_t length = header + 4u * data[offset + 1];
    return Fits(end, offset, length);
}

inline bool Acl(const std::uint8_t* data, std::size_t size, std::size_t offset, bool system) noexcept {
    if (!Fits(size, offset, sizeof(ACL))) return false;
    const std::size_t length = U16(data, offset + 2);
    const unsigned count = U16(data, offset + 4);
    if (length < sizeof(ACL) || !Fits(size, offset, length)) return false;
    const std::size_t end = offset + length;
    std::size_t cursor = offset + sizeof(ACL);
    for (unsigned i = 0; i < count; ++i) {
        if (!Fits(end, cursor, sizeof(ACE_HEADER))) return false;
        const std::size_t aceSize = U16(data, cursor + 2);
        if (aceSize < sizeof(ACE_HEADER) || (aceSize & 3u) != 0 || !Fits(end, cursor, aceSize)) return false;
        const std::size_t aceEnd = cursor + aceSize;
        bool object = false;
        switch (data[cursor]) {
        case ACCESS_ALLOWED_ACE_TYPE:
        case ACCESS_DENIED_ACE_TYPE:
            if (system) return false;
            break;
        case ACCESS_ALLOWED_OBJECT_ACE_TYPE:
        case ACCESS_DENIED_OBJECT_ACE_TYPE:
            if (system) return false;
            object = true;
            break;
        case SYSTEM_AUDIT_ACE_TYPE:
            if (!system) return false;
            break;
        case SYSTEM_AUDIT_OBJECT_ACE_TYPE:
            if (!system) return false;
            object = true;
            break;
        default:
            // Callback/conditional, resource-attribute, label, reserved and
            // unknown ACEs have type-specific semantics or payloads that
            // IsValidAcl does not validate. Refuse, even if otherwise valid,
            // rather than silently claim these types were checked.
            return false;
        }
        std::size_t sidOffset = sizeof(ACE_HEADER) + sizeof(ACCESS_MASK);
        if (object) {
            if (data[offset] != ACL_REVISION_DS || !Fits(aceEnd, cursor, sidOffset + sizeof(DWORD))) return false;
            const std::uint32_t flags = U32(data, cursor + sidOffset);
            if (flags & ~(ACE_OBJECT_TYPE_PRESENT | ACE_INHERITED_OBJECT_TYPE_PRESENT)) return false;
            sidOffset += sizeof(DWORD);
            if (flags & ACE_OBJECT_TYPE_PRESENT) sidOffset += sizeof(GUID);
            if (flags & ACE_INHERITED_OBJECT_TYPE_PRESENT) sidOffset += sizeof(GUID);
        }
        if (!Fits(aceEnd, cursor, sidOffset) || !Sid(data, aceEnd, cursor + sidOffset)) return false;
        cursor = aceEnd;
    }
    return true;
}
} // namespace DescriptorDetail

// Structural validation of the supported, self-relative representation only;
// it is not a DCOM authorization/policy decision or a source-trust check.
// The caller must keep [data, data+size) readable and immutable for this call.
// Win32 requires DWORD-aligned descriptor/ACL/ACE addresses; other input is refused.
// Accepted input is never rewritten, canonicalized, or copied to the registry.
inline bool BoundedSecurityDescriptor(const std::uint8_t* data, std::size_t size) noexcept {
    using namespace DescriptorDetail;
    static_assert(sizeof(SECURITY_DESCRIPTOR_RELATIVE) == 20, "Unexpected Windows SD layout");
    if (!data || !Fits(size, 0, sizeof(SECURITY_DESCRIPTOR_RELATIVE)) || data[0] != SECURITY_DESCRIPTOR_REVISION) return false;
    const std::uint16_t control = U16(data, 2);
    if (!(control & SE_SELF_RELATIVE)) return false;
    const std::uint32_t owner = U32(data, 4), group = U32(data, 8);
    const std::uint32_t sacl = U32(data, 12), dacl = U32(data, 16);
    const std::uint32_t offsets[] = {owner, group, sacl, dacl};
    for (std::uint32_t offset : offsets) {
        if (offset && (offset < sizeof(SECURITY_DESCRIPTOR_RELATIVE) || (offset & 3u) != 0)) return false;
    }
    if ((owner && !Sid(data, size, owner)) || (group && !Sid(data, size, group))) return false;
    if (!(control & SE_SACL_PRESENT) && sacl) return false;
    if (!(control & SE_DACL_PRESENT) && dacl) return false;
    if ((sacl && !Acl(data, size, sacl, true)) || (dacl && !Acl(data, size, dacl, false))) return false;

    // Win32's pointer-only validators do not know the allocation length. Call
    // them only after *all* referenced headers, SIDs and ACEs were bounded.
    if (reinterpret_cast<std::uintptr_t>(data) % alignof(DWORD) != 0) return false;
    auto* base = const_cast<std::uint8_t*>(data);
    if ((sacl && !IsValidAcl(reinterpret_cast<PACL>(base + sacl))) ||
        (dacl && !IsValidAcl(reinterpret_cast<PACL>(base + dacl)))) return false;
    return IsValidSecurityDescriptor(base) != FALSE;
}
} // namespace IcemOpc
