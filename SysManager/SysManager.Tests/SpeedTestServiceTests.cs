// SysManager · SpeedTestServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="SpeedTestService"/>'s Zip Slip containment check, which
/// guards the Ookla CLI extraction against crafted archive entries escaping the
/// target tools directory.
/// </summary>
public class SpeedTestServiceTests
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "smtest_tools");

    [Fact]
    public void IsInsideDirectory_NormalEntry_IsAccepted()
    {
        var dest = Path.GetFullPath(Path.Join(Root, "speedtest.exe"));
        Assert.True(SpeedTestService.IsInsideDirectory(Root, dest));
    }

    [Fact]
    public void IsInsideDirectory_NestedEntry_IsAccepted()
    {
        var dest = Path.GetFullPath(Path.Join(Root, "sub", "license.txt"));
        Assert.True(SpeedTestService.IsInsideDirectory(Root, dest));
    }

    [Fact]
    public void IsInsideDirectory_TraversalEntry_IsRejected()
    {
        // A "../" entry that resolves to the parent directory must be rejected.
        var dest = Path.GetFullPath(Path.Join(Root, "..", "evil.exe"));
        Assert.False(SpeedTestService.IsInsideDirectory(Root, dest));
    }

    [Fact]
    public void IsInsideDirectory_SiblingWithSharedPrefix_IsRejected()
    {
        // Regression: a sibling directory whose name merely starts with the target's
        // name (e.g. "smtest_tools-evil") must NOT pass the containment check. A naive
        // StartsWith(fullToolsDir) without a trailing separator would wrongly accept it.
        var dest = Path.GetFullPath(Path.Combine(Root + "-evil", "payload.exe"));
        Assert.False(SpeedTestService.IsInsideDirectory(Root, dest));
    }

    [Fact]
    public async Task RunOoklaAsync_UserCancelDuringPrepare_SurfacesAsCancellation()
    {
        // Regression: the prepare phase (EnsureOoklaAsync) was wrapped in a blanket
        // catch (Exception) that re-threw OperationCanceledException as
        // InvalidOperationException("Could not prepare Ookla CLI: A task was canceled."),
        // bypassing the ViewModel's dedicated "Cancelled" handler and misreporting a
        // clean user cancel as an error. A pre-cancelled token makes the first
        // Task.Run(..., ct) inside EnsureOoklaAsync throw before any network or
        // filesystem work, so this test is deterministic and offline.
        var svc = new SpeedTestService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.RunOoklaAsync(progress: null, cts.Token));
    }    // ---------- the Ookla CLI pin: verified object == executed object ----------

    [Fact]
    public void Pin_WhileHeld_RefusesOverwriteAndRename()
    {
        // The whole point of the pin. The CLI lives in a user-writable directory, so without this a path
        // verified a moment ago can name different bytes by the time Process.Start reads it — and when
        // SysManager is elevated, those bytes run at high integrity.
        var dir = Path.Combine(Path.GetTempPath(), "smtest_pin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "pinned.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4]);

        try
        {
            using var pin = SpeedTestService.Pin(file);

            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(file, [9, 9, 9, 9]));
            Assert.ThrowsAny<IOException>(() => File.Move(file, Path.Combine(dir, "swapped.bin")));
            Assert.ThrowsAny<IOException>(() => File.Delete(file));

            // And the original bytes are intact, not merely un-renamed.
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(file));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ } }
    }

    [Fact]
    public void Pin_WhileHeld_StillAllowsReading()
    {
        // The opposite failure, and the reason this is FileShare.Read rather than FileShare.None: a pin
        // that denied reading would stop the Windows loader mapping the image, so the speed test could
        // never run. Reading is what the loader needs, so reading must stay possible.
        var dir = Path.Combine(Path.GetTempPath(), "smtest_pinread_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "pinned.bin");
        File.WriteAllBytes(file, [7, 7, 7]);

        try
        {
            using var pin = SpeedTestService.Pin(file);

            using var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Equal(3, reader.Length);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ } }
    }

    [Fact]
    public void PinAndVerify_UnsignedBinary_ThrowsAndDeletesIt()
    {
        // Regression for a defect the pin itself introduced and that had to be designed out.
        //
        // VerifyOoklaSignature is fail-closed and used to delete the rejected binary itself, through
        // TryDeleteExe — which swallows IOException at Debug level. Pinning the file against deletion
        // during verification made every one of those deletes fail silently: the exception still said
        // "Binary deleted for security" while the rejected file stayed in the cache indefinitely.
        //
        // The delete now happens in PinAndVerify's finally, after the pin is released. Reverse that order
        // and this test goes red.
        var dir = Path.Combine(Path.GetTempPath(), "smtest_pinverify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "speedtest.exe");
        File.WriteAllBytes(file, [0x4D, 0x5A, 0x00, 0x01, 0x02, 0x03]);   // "MZ" and nothing else: no signature

        try
        {
            Assert.Throws<InvalidOperationException>(() => SpeedTestService.PinAndVerify(file));
            Assert.False(File.Exists(file),
                "verification rejected the binary but it is still on disk — the pin was not released before the delete");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ } }
    }
}
