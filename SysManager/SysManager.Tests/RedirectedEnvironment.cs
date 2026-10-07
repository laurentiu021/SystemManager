// SysManager · RedirectedEnvironment
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Microsoft.Win32;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// An <see cref="EnvironmentVariableService"/> over two throwaway keys under HKCU standing in for the user's and the
/// machine's environment, and a temp folder for the legacy backup file. Deleted on dispose.
/// </summary>
/// <remarks>
/// Shared by the environment tests and by Undo Changes' (#1525), which compares and restores the same copies. Never
/// touches the real environment of either scope.
/// </remarks>
internal sealed class RedirectedEnvironment : IDisposable
{
    private readonly string _userRootName =
        $@"Software\SysManagerTests\Environment\User_{Guid.NewGuid():N}";
    private readonly string _machineRootName =
        $@"Software\SysManagerTests\Environment\Machine_{Guid.NewGuid():N}";

    public RedirectedEnvironment(bool enforceMachineBackupProtection = false)
    {
        BackupDirectory = Path.Combine(
            Path.GetTempPath(),
            $"SysManagerEnvironmentTests_{Guid.NewGuid():N}");
        UserRoot = Registry.CurrentUser.CreateSubKey(_userRootName, writable: true)
            ?? throw new InvalidOperationException("Could not create redirected User root.");
        MachineRoot = Registry.CurrentUser.CreateSubKey(_machineRootName, writable: true)
            ?? throw new InvalidOperationException("Could not create redirected Machine root.");

        using var userEnvironment = UserRoot.CreateSubKey(
            EnvironmentVariableService.UserEnvPath,
            writable: true);
        using var machineEnvironment = MachineRoot.CreateSubKey(
            EnvironmentVariableService.MachineEnvPath,
            writable: true);

        Service = new EnvironmentVariableService(
            BackupDirectory,
            UserRoot,
            MachineRoot,
            enforceMachineBackupProtection);
    }

    public string BackupDirectory { get; }
    public RegistryKey UserRoot { get; }
    public RegistryKey MachineRoot { get; }
    public EnvironmentVariableService Service { get; }

    public void WriteUserBackup(string json)
        => WriteLegacyUserBackup(json);

    public void WriteLegacyUserBackup(string json)
    {
        Directory.CreateDirectory(BackupDirectory);
        File.WriteAllText(Service.BackupPath, json);
    }

    public void WriteUserRegistryBackup(object value, RegistryValueKind kind = RegistryValueKind.String)
    {
        using var key = UserRoot.CreateSubKey(
            EnvironmentVariableService.UserBackupPath,
            writable: true);
        key!.SetValue(EnvironmentVariableService.UserBackupValueName, value, kind);
    }

    public bool HasUserRegistryBackup()
    {
        using var key = UserRoot.OpenSubKey(EnvironmentVariableService.UserBackupPath);
        return key?.GetValueNames().Contains(
            EnvironmentVariableService.UserBackupValueName,
            StringComparer.OrdinalIgnoreCase) == true;
    }

    public object? GetUserBackupRaw()
    {
        using var key = UserRoot.OpenSubKey(EnvironmentVariableService.UserBackupPath);
        return key?.GetValue(
            EnvironmentVariableService.UserBackupValueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public void WriteMachineBackup(object value, RegistryValueKind kind)
    {
        using var key = MachineRoot.CreateSubKey(
            EnvironmentVariableService.MachineBackupPath,
            writable: true);
        key!.SetValue(EnvironmentVariableService.MachineBackupValueName, value, kind);
    }

    public object? GetMachineBackupRaw()
    {
        using var key = MachineRoot.OpenSubKey(EnvironmentVariableService.MachineBackupPath);
        return key?.GetValue(
            EnvironmentVariableService.MachineBackupValueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public void SetUser(string name, string value)
        => SetUser(name, value, RegistryValueKind.String);

    public void SetUser(string name, object value, RegistryValueKind kind)
    {
        using var key = UserRoot.OpenSubKey(
            EnvironmentVariableService.UserEnvPath,
            writable: true);
        key!.SetValue(name, value, kind);
    }

    public void SetMachine(string name, string value)
        => SetMachine(name, value, RegistryValueKind.String);

    public void SetMachine(string name, object value, RegistryValueKind kind)
    {
        using var key = MachineRoot.OpenSubKey(
            EnvironmentVariableService.MachineEnvPath,
            writable: true);
        key!.SetValue(name, value, kind);
    }

    public object? GetUser(string name)
    {
        using var key = UserRoot.OpenSubKey(EnvironmentVariableService.UserEnvPath);
        return key?.GetValue(name, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public object? GetMachine(string name)
    {
        using var key = MachineRoot.OpenSubKey(EnvironmentVariableService.MachineEnvPath);
        return key?.GetValue(name, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public void Dispose()
    {
        UserRoot.Dispose();
        MachineRoot.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_userRootName, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(_machineRootName, throwOnMissingSubKey: false);
        if (Directory.Exists(BackupDirectory))
            Directory.Delete(BackupDirectory, recursive: true);
    }
}
