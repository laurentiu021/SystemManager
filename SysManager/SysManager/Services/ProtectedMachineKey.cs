// SysManager · ProtectedMachineKey
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace SysManager.Services;

/// <summary>
/// A key directly under HKLM\SOFTWARE that only administrators and Windows can change: created with that access list,
/// and checked, with its parent, before anything kept in it is trusted.
/// </summary>
/// <remarks>
/// <para>Held once so every store that keeps what an elevated put-back reads applies the same checks. It lives in
/// Services/, not Helpers/, because its enforced create path runs only against the real HKLM with administrator rights,
/// which no unit test reaches, and Helpers/ is held to a line-coverage floor for code with nothing of the machine
/// behind it.</para>
/// <para>The <c>label</c> each caller passes names its key in the exceptions, so a message reads as it did when the
/// checks lived in the one store that had them.</para>
/// </remarks>
internal static class ProtectedMachineKey
{
    /// <summary>
    /// Opens the key at <paramref name="path"/> under <paramref name="machineRoot"/> for writing, creating it when it
    /// is not there. With <paramref name="enforce"/>, the parent must already be protected, and the key is created with
    /// the protected access list and checked once created.
    /// </summary>
    /// <param name="machineRoot">HKLM, or the hive standing in for it in a test.</param>
    /// <param name="path">The key, directly under SOFTWARE.</param>
    /// <param name="enforce">Whether the key and its parent must be access-controlled.</param>
    /// <param name="label">What the key holds, as the exceptions name it.</param>
    /// <exception cref="InvalidDataException">
    /// The key or its parent is not protected from standard-user writes.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The key could not be created or opened for writing.</exception>
    internal static RegistryKey OpenOrCreate(RegistryKey machineRoot, string path, bool enforce, string label)
    {
        if (!enforce)
        {
            return machineRoot.CreateSubKey(path, writable: true)
                ?? throw new UnauthorizedAccessException($"The protected {label} key could not be created.");
        }

        if (!IsParentProtected(machineRoot))
            throw new InvalidDataException($"The {label} parent key is not protected from standard-user writes.");

        using (var key = machineRoot.CreateSubKey(
                   path,
                   RegistryKeyPermissionCheck.ReadWriteSubTree,
                   RegistryOptions.None,
                   CreateSecurity())
               ?? throw new UnauthorizedAccessException($"The protected {label} key could not be created."))
        {
            if (!IsProtected(SecurityOf(key)))
                throw new InvalidDataException($"The {label} key is not protected from standard-user writes.");
        }

        return machineRoot.OpenSubKey(path, writable: true)
            ?? throw new UnauthorizedAccessException($"The protected {label} key could not be reopened.");
    }

    /// <summary>True when only trusted principals can change SOFTWARE under <paramref name="machineRoot"/>.</summary>
    internal static bool IsParentProtected(RegistryKey machineRoot)
    {
        using var parent = machineRoot.OpenSubKey(
            "SOFTWARE",
            RegistryRights.ReadKey | RegistryRights.ReadPermissions);
        return parent is not null && IsProtected(SecurityOf(parent));
    }

    /// <summary>
    /// The access list a protected key is created with: owned by Administrators, full control for Administrators and
    /// SYSTEM, read for Users, nothing inherited.
    /// </summary>
    internal static RegistrySecurity CreateSecurity()
    {
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            domainSid: null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null);

        RegistrySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        security.AddAccessRule(new RegistryAccessRule(
            administrators,
            RegistryRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new RegistryAccessRule(
            system,
            RegistryRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new RegistryAccessRule(
            users,
            RegistryRights.ReadKey,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// True when <paramref name="security"/> has a trusted owner, canonical rules, and no allow rule that lets anyone
    /// but Administrators, SYSTEM or the creator-owner placeholder change the key.
    /// </summary>
    internal static bool IsProtected(RegistrySecurity security)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !IsTrustedPrincipal(owner) ||
            !security.AreAccessRulesCanonical)
            return false;

        const RegistryRights mutatingRights =
            RegistryRights.SetValue |
            RegistryRights.CreateSubKey |
            RegistryRights.CreateLink |
            RegistryRights.Delete |
            RegistryRights.ChangePermissions |
            RegistryRights.TakeOwnership;

        foreach (RegistryAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                (rule.RegistryRights & mutatingRights) == 0 ||
                (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
            {
                continue;
            }

            var principal = (SecurityIdentifier)rule.IdentityReference;
            if (!IsTrustedPrincipal(principal) &&
                !principal.IsWellKnown(WellKnownSidType.CreatorOwnerSid))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTrustedPrincipal(SecurityIdentifier principal) =>
        principal.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
        principal.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);

    private static RegistrySecurity SecurityOf(RegistryKey key) =>
        key.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
}
