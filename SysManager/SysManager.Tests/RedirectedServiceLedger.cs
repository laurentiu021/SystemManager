// SysManager · RedirectedServiceLedger
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// A <see cref="ServiceStartupLedgerService"/> over a throwaway key under HKCU standing in for HKLM, and a folder for
/// the file earlier versions kept the records in. The key is deleted on dispose, and the folder too when this made it.
/// </summary>
/// <remarks>
/// The stand-in's SOFTWARE key is the test account's own, so it is never protected: built with
/// <c>enforceProtection: true</c>, the ledger refuses to keep a record in it and reads what it holds as unusable, which
/// is how it treats a key other programs could change.
/// </remarks>
internal sealed class RedirectedServiceLedger : IDisposable
{
    private readonly string _rootName = $@"Software\SysManagerTests\ServiceLedger\Machine_{Guid.NewGuid():N}";
    private readonly bool _ownsFolder;

    public RedirectedServiceLedger(string? folder = null, bool enforceProtection = false)
    {
        _ownsFolder = folder is null;
        Folder = folder ?? Path.Combine(Path.GetTempPath(), "SysManagerLedgerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        MachineRoot = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)
            ?? throw new InvalidOperationException("Could not create the stand-in machine root.");
        EnforceProtection = enforceProtection;
    }

    public string Folder { get; }
    public RegistryKey MachineRoot { get; }
    public bool EnforceProtection { get; }

    /// <summary>When set, every read of the protected key throws it, as Windows can refuse a read.</summary>
    public Exception? ReadFailure { get; set; }

    /// <summary>The file earlier versions kept the records in.</summary>
    public string LegacyPath => Path.Combine(Folder, ServiceStartupLedgerService.LegacyFileName);

    /// <summary>The marks a look without administrator rights leaves in the profile.</summary>
    public string StalePath => Path.Combine(Folder, ServiceStartupLedgerService.StaleFileName);

    /// <summary>A fresh instance over the same key and folder, as the next look or launch would build.</summary>
    public ServiceStartupLedgerService NewService() => new(Folder, MachineRoot, EnforceProtection,
        (root, path) => ReadFailure is { } failure ? throw failure : root.OpenSubKey(path));

    /// <summary>Writes the file an earlier version kept, holding <paramref name="records"/>.</summary>
    public void WriteLegacy(params ServiceStartupRecord[] records) =>
        File.WriteAllText(LegacyPath, ServiceStartupLedgerService.Serialize(
            records.ToDictionary(r => r.ServiceName, StringComparer.OrdinalIgnoreCase)));

    /// <summary>The services the earlier version's file names now.</summary>
    public IReadOnlyCollection<string> LegacyNames() =>
        ServiceStartupLedgerService.Parse(File.Exists(LegacyPath) ? File.ReadAllText(LegacyPath) : null).Keys.ToList();

    /// <summary>Plants <paramref name="value"/> in the protected key, under <paramref name="name"/>.</summary>
    public void WriteProtected(object value, RegistryValueKind kind = RegistryValueKind.String,
        string name = ServiceStartupLedgerService.ProtectedValueName)
    {
        using var key = MachineRoot.CreateSubKey(ServiceStartupLedgerService.ProtectedPath, writable: true);
        key!.SetValue(name, value, kind);
    }

    /// <summary>The value <paramref name="name"/> in the protected key as stored, or null when not there.</summary>
    public object? ProtectedRaw(string name = ServiceStartupLedgerService.ProtectedValueName)
    {
        using var key = MachineRoot.OpenSubKey(ServiceStartupLedgerService.ProtectedPath);
        return key?.GetValue(name, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    /// <summary>The services the protected key names, read straight from the key.</summary>
    public IReadOnlyCollection<string> ProtectedNames() =>
        ServiceStartupLedgerService.Parse(ProtectedRaw() as string).Keys.ToList();

    /// <summary>
    /// Refuses this account every change to the protected key, the way Windows refuses a standard user, until the
    /// returned scope is disposed.
    /// </summary>
    public IDisposable DenyWrites()
    {
        var key = MachineRoot.CreateSubKey(ServiceStartupLedgerService.ProtectedPath, writable: true)
            ?? throw new InvalidOperationException("Could not create the stand-in ledger key.");
        using var identity = WindowsIdentity.GetCurrent();
        var deny = new RegistryAccessRule(
            identity.User!,
            RegistryRights.SetValue | RegistryRights.CreateSubKey,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);
        var denied = key.GetAccessControl(AccessControlSections.Access);
        denied.AddAccessRule(deny);
        key.SetAccessControl(denied);
        return new Restore(key, deny);
    }

    public void Dispose()
    {
        MachineRoot.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false);
        if (_ownsFolder && Directory.Exists(Folder))
        {
            try { Directory.Delete(Folder, recursive: true); }
            catch (IOException) { /* a leftover temp folder must never fail a test run */ }
        }
    }

    /// <summary>Takes the deny rule out again.</summary>
    /// <remarks>
    /// By removing the rule, not by writing back the list read before it was added: a list that was read and not
    /// changed marks no section as changed, so writing it back writes nothing and the rule stays.
    /// </remarks>
    private sealed class Restore(RegistryKey key, RegistryAccessRule deny) : IDisposable
    {
        public void Dispose()
        {
            var security = key.GetAccessControl(AccessControlSections.Access);
            security.RemoveAccessRuleSpecific(deny);
            key.SetAccessControl(security);
            key.Dispose();
        }
    }
}
