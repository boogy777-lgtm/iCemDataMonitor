using System.Security.AccessControl;
using System.Security.Principal;

namespace iCemDataMonitor.Data.Bus;

/// <summary>
/// Fatal-fast open failure of the PERSISTED bus mapping: the physical file's
/// DACL (installed by a process running in a different security context —
/// elevated or another account) does not grant THIS process the rights needed
/// to map a ReadWrite view. The bus is a CROSS-PROCESS contract; the creator
/// of the file must install the operator-class DACL. This exception carries
/// the full operator remediation — path, current identity, the file's current
/// ACL, and the fix — so the operator acts without a debugger.
/// </summary>
public sealed class BusAccessDeniedException : UnauthorizedAccessException
{
    public BusAccessDeniedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    /// <summary>
    /// Build the actionable message for the READ-denied mode: the physical
    /// file's DACL (installed by a process running in a different security
    /// context — another account) does not grant this READ-ONLY consumer even
    /// read access. Remediation is producer-side: the AddIn installs
    /// BUILTIN\Users:Modify at creation, so a foreign-context file must be
    /// deleted and recreated by the producer (or granted Read manually).
    /// </summary>
    public static BusAccessDeniedException ForPath(string path, Exception? inner = null)
    {
        var message = string.Join(Environment.NewLine, new[]
        {
            "iCemDataMonitor bus: ACCESS DENIED — the bus file cannot be opened for READ mapping.",
            $"  Path:             {path}",
            $"  Current identity: {CurrentIdentity()}",
            $"  File ACL (allow): {DescribeFileAcl(path)}",
            "Cause: the file was created by a process running in a different security context",
            "(elevated or another account), so its DACL does not grant this process READ access.",
            "Remediation: delete the file and let the producer AddIn recreate it (it installs",
            "BUILTIN\\Users:Modify at creation), OR grant BUILTIN\\Users the Read right — then",
            "restart the host.",
            "Note: the named bus section keeps the old ACL until every handle is closed, so a",
            "restart is required after either fix.",
        });
        return new BusAccessDeniedException(message, inner);
    }

    /// <summary>
    /// Build the actionable message for the CAPACITY-SKEW mode: the file ACL
    /// is fine (a 1-byte ReadWrite view succeeds — verified by the probe
    /// discriminator), but the LIVE named section was created by an
    /// older-dictionary producer and keeps its creation-time capacity until
    /// EVERY handle closes. Windows reports ACCESS DENIED for views beyond the
    /// section capacity, so this masquerades as a permission error while the
    /// real cause is a dictionary-size skew between the live section and the
    /// expanded on-disk file. Remediation is an ordered restart: producer
    /// FIRST, consumers next.
    /// </summary>
    public static BusAccessDeniedException ForSectionCapacity(string path, long requiredBytes, Exception? inner = null)
    {
        var message = string.Join(Environment.NewLine, new[]
        {
            "iCemDataMonitor bus: ACCESS DENIED — the LIVE named section capacity is SMALLER than this process requires.",
            $"  Path:            {path}",
            $"  Required bytes:  {requiredBytes}",
            $"  Current identity: {CurrentIdentity()}",
            "Cause: the file ACL is fine, but the live section was created by an older-dictionary",
            "producer at a smaller capacity and keeps that capacity until every handle closes.",
            "(Windows reports ACCESS DENIED for views beyond the section capacity, so this",
            "surfaces as a permission error while the real cause is a dictionary-size skew.)",
            "Remediation: the named section is held open at a smaller capacity by another process",
            "(producer AddIn or a stale consumer): stop ALL station processes incl. the producer",
            "(iCem.exe), then start the producer FIRST, then the consumers.",
        });
        return new BusAccessDeniedException(message, inner);
    }

    private static string CurrentIdentity()
    {
        if (!OperatingSystem.IsWindows())
            return "(not available — non-Windows host)";
        try
        {
            return WindowsIdentity.GetCurrent().Name;
        }
        catch
        {
            return "(unreadable)";
        }
    }

    private static string DescribeFileAcl(string path)
    {
        if (!OperatingSystem.IsWindows())
            return "(not available — non-Windows host)";
        return DescribeFileAclWindows(path);
    }

    /// <summary>Summarize the file's ALLOW ACEs as "identity: rights; …" (operator-comparable).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string DescribeFileAclWindows(string path)
    {
        try
        {
            var security = FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
            var parts = new List<string>();
            foreach (FileSystemAccessRule rule in security
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow)
                    continue;
                parts.Add($"{DescribeRef(rule.IdentityReference)}: {rule.FileSystemRights}");
            }
            return parts.Count > 0 ? string.Join("; ", parts) : "(empty)";
        }
        catch
        {
            return "(unreadable — READ_CONTROL denied)";
        }
    }

    /// <summary>NTAccount name when resolvable; raw SID string otherwise (never throws).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string DescribeRef(IdentityReference reference)
    {
        try
        {
            return reference.Translate(typeof(NTAccount)).Value;
        }
        catch
        {
            return reference.Value;
        }
    }
}
