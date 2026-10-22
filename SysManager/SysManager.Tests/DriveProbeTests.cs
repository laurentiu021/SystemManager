// SysManager · DriveProbeTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DriveProbe.ReadFixed"/>, the read the Dashboard's storage card and About's environment info
/// both make of every drive (#2802), over stand-in drives, so nothing here waits on a real one.
/// </summary>
/// <remarks>
/// Both asked every letter whether it was ready before its type ruled it out, and held all the drives in one
/// <c>try</c>, so the first drive that threw ended the list.
/// </remarks>
public class DriveProbeTests
{
    private static DriveProbe Probe(
        string name, DriveType type, Func<bool>? ready = null, Func<long>? total = null) =>
        new(name, type, ready ?? (() => true), total ?? (() => 1L << 30), () => 1L << 29, () => "NTFS");

    [Fact]
    public void OnlyAFixedDrive_IsAskedWhetherItIsReady()
    {
        List<string> asked = [];
        Func<bool> Ready(string name) => () => { asked.Add(name); return true; };

        var read = DriveProbe.ReadFixed(
        [
            Probe(@"A:\", DriveType.Removable, Ready(@"A:\")),
            Probe(@"C:\", DriveType.Fixed, Ready(@"C:\")),
            Probe(@"D:\", DriveType.CDRom, Ready(@"D:\")),
            Probe(@"Z:\", DriveType.Network, Ready(@"Z:\")),
            Probe(@"E:\", DriveType.Fixed, Ready(@"E:\")),
        ], drive => drive.Name);

        Assert.Equal([@"C:\", @"E:\"], asked);
        Assert.Equal([@"C:\", @"E:\"], read);
    }

    [Fact]
    public void ADriveThatIsNotReady_IsLeftOut_AndNotRead()
    {
        List<string> sized = [];
        long Size(string name) { sized.Add(name); return 1L << 30; }

        var read = DriveProbe.ReadFixed(
        [
            Probe(@"C:\", DriveType.Fixed, total: () => Size(@"C:\")),
            Probe(@"E:\", DriveType.Fixed, ready: () => false, total: () => Size(@"E:\")),
        ], drive => drive.TotalSize());

        Assert.Single(read);
        Assert.Equal([@"C:\"], sized);
    }

    [Theory]
    [InlineData(typeof(IOException))]                  // the drive went away between the two reads
    [InlineData(typeof(UnauthorizedAccessException))]  // the drive refused them
    public void ADriveThatCannotBeRead_IsLeftOut_AndTheRestAreRead(Type failure)
    {
        var read = DriveProbe.ReadFixed(
        [
            Probe(@"C:\", DriveType.Fixed),
            Probe(@"D:\", DriveType.Fixed, total: () => throw (Exception)Activator.CreateInstance(failure)!),
            Probe(@"E:\", DriveType.Fixed),
        ], drive => $"{drive.Name} {drive.TotalSize()}");

        Assert.Equal([$@"C:\ {1L << 30}", $@"E:\ {1L << 30}"], read);
    }

    [Fact]
    public void Of_TakesTheDrivesNameAndType()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;

        var probe = DriveProbe.Of(new DriveInfo(root));

        Assert.Equal(root, probe.Name);
        Assert.Equal(DriveType.Fixed, probe.Type);
    }
}
