// SysManager · SingleInstancePipeTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SysManager.IntegrationTests;

/// <summary>
/// A start that finds a running copy's mutex or pipe it may not reach, on a real mutex and a real named pipe (#2799).
/// </summary>
/// <remarks>
/// Both belonged to whichever account started first, so a start under another account was refused the mutex, or the
/// connect to the pipe, which admits only the account that made it, and the refusal escaped before logging existed:
/// SysManager simply never opened. Here the mutex and the pipe admit only LocalSystem, which the account running the
/// tests is not, so they refuse it the same way. Their names are this test's own, and they are gone when it ends.
/// </remarks>
public class SingleInstancePipeTests
{
    [Fact]
    public void TakeInstanceMutex_AMutexThisAccountMayNotOpen_RunsWithoutIt_NotAnException()
    {
        var name = $@"Local\SysManager_SingleInstance_Test_{Guid.NewGuid():N}";
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            MutexRights.FullControl,
            AccessControlType.Allow));
        using var held = MutexAcl.Create(initiallyOwned: false, name, out var made, security);
        Assert.True(made);

        // The mutex refuses this account, or the case below would prove nothing about a refusal.
        Assert.Throws<UnauthorizedAccessException>(() => new Mutex(true, name, out _).Dispose());

        using var mutex = App.TakeInstanceMutex(name, out var createdNew, out var refused);

        Assert.Null(mutex);
        Assert.True(createdNew);   // this copy goes on as its own
        Assert.False(string.IsNullOrEmpty(refused));
    }

    [Fact]
    public void SignalThroughPipe_APipeThisAccountMayNotReach_IsFalse_NotAnException()
    {
        var name = "SysManager_SingleInstance_Test_" + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        using var server = NamedPipeServerStreamAcl.Create(
            name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);

        void Connect()
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
            client.Connect(timeout: 2000);
        }

        // The pipe refuses this account, or the case below would prove nothing about a refusal.
        Assert.Throws<UnauthorizedAccessException>(Connect);
        Assert.False(App.SignalThroughPipe(Connect));
    }
}
