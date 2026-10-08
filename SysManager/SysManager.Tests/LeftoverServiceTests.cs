// SysManager · LeftoverServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Removing what an uninstall left behind (#1527): folders go to the Recycle Bin, keys are exported before they are
/// deleted, every folder is checked again just before it goes, and what needs administrator rights is remembered.
/// The Recycle Bin, the key deletion and <c>reg.exe</c> are all recorders, so nothing of the PC running the suite
/// is sent anywhere, deleted or exported.
/// </summary>
public sealed class LeftoverServiceTests : IDisposable
{
    private readonly TempLeftoverEnvironment _env = new();
    private readonly IPowerShellRunner _runner = Substitute.For<IPowerShellRunner>();
    private readonly List<string> _recycled = [];
    private readonly List<string> _deletedKeys = [];

    public void Dispose() => _env.Dispose();

    private LeftoverService Service(bool recycleWorks = true, bool deleteWorks = true) =>
        new(_runner, _env,
            recycle: path => { _recycled.Add(path); return recycleWorks; },
            deleteKey: key => { _deletedKeys.Add(key); return deleteWorks; });

    private static LeftoverItem Folder(string path, LeftoverConfidence confidence = LeftoverConfidence.Certain,
                                       long size = 0, bool needsAdmin = false) =>
        new() { Location = path, Kind = LeftoverKind.Folder, Confidence = confidence, SizeBytes = size, NeedsAdministrator = needsAdmin };

    private static LeftoverItem Key(string key) =>
        new() { Location = key, Kind = LeftoverKind.RegistryKey, Confidence = LeftoverConfidence.OwnKey };

    /// <summary>Makes <c>reg.exe export</c> answer <paramref name="exitCode"/>, writing <paramref name="content"/> to the file it names.</summary>
    private void RegExport(int exitCode, string? content)
    {
        _runner.RunProcessAsync("reg.exe", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
            .Returns(call =>
            {
                if (content is not null) File.WriteAllText(ExportTarget(call.ArgAt<string>(1)), content);
                return Task.FromResult(exitCode);
            });
    }

    /// <summary>The file a <c>reg export "HKCU\…" "file" /y</c> command line writes to.</summary>
    private static string ExportTarget(string arguments) => arguments.Split('"')[3];

    // ── Finding ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Find_MeasuresEachFolder()
    {
        TempLeftoverEnvironment.Folder(Path.Combine(_env.RoamingAppData, "Acme Notes"), bytes: 1234);

        var group = await Service().FindAsync(new UninstallProbe("Acme Notes", "Acme", "", ""), []);

        Assert.Equal("Acme Notes", group.AppName);
        Assert.Equal(1234, Assert.Single(group.Items).SizeBytes);
    }

    [Fact]
    public async Task Find_RefusesAFolderThatHoldsALink()
    {
        var folder = TempLeftoverEnvironment.Folder(Path.Combine(_env.RoamingAppData, "Acme Notes"), bytes: 10);
        var elsewhere = TempLeftoverEnvironment.Folder(Path.Combine(_env.Root, "Elsewhere"), bytes: 10);
        Symlinks.RequireJunction(Path.Combine(folder, "cache"), elsewhere);

        var group = await Service().FindAsync(new UninstallProbe("Acme Notes", "Acme", "", ""), []);

        Assert.Empty(group.Items);
        Assert.Contains($"Not offered: {folder} holds a link to another folder.", group.NotOffered);
    }

    // ── Folders ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AFolder_GoesToTheRecycleBin()
    {
        var path = TempLeftoverEnvironment.Folder(Path.Combine(_env.RoamingAppData, "Acme Notes"), bytes: 10);
        var item = Folder(path, size: 10);

        var result = await Service().RemoveAsync([item]);

        Assert.Equal([path], _recycled);
        Assert.Same(item, Assert.Single(result.Removed));
        Assert.Equal(10, result.BytesFreed);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task AFolderTheRecycleBinWouldNotTake_IsNotCountedAsRemoved()
    {
        var path = TempLeftoverEnvironment.Folder(Path.Combine(_env.RoamingAppData, "Acme Notes"));

        var result = await Service(recycleWorks: false).RemoveAsync([Folder(path, size: 10)]);

        Assert.Empty(result.Removed);
        Assert.Equal(0, result.BytesFreed);
        Assert.Equal($"{path}: Windows did not send it to the Recycle Bin.", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task AFolderThatBecameALinkSinceTheSearch_IsRefused_AndNeverHandedToTheRecycleBin()
    {
        var path = Path.Combine(_env.RoamingAppData, "Acme Notes");
        var victim = TempLeftoverEnvironment.Folder(Path.Combine(_env.Documents, "Taxes"), bytes: 10);
        Symlinks.RequireJunction(path, victim);   // swapped in after the folder was found

        var result = await Service().RemoveAsync([Folder(path)]);

        Assert.Empty(_recycled);
        Assert.Empty(result.Removed);
        Assert.Equal($"{path}: not removed, because it is a link to another folder.", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task AFolderThatGainedALinkInsideSinceTheSearch_IsRefused()
    {
        var path = TempLeftoverEnvironment.Folder(Path.Combine(_env.RoamingAppData, "Acme Notes"));
        var victim = TempLeftoverEnvironment.Folder(Path.Combine(_env.Documents, "Taxes"), bytes: 10);
        Symlinks.RequireJunction(Path.Combine(path, "cache"), victim);

        var result = await Service().RemoveAsync([Folder(path)]);

        Assert.Empty(_recycled);
        Assert.Equal($"{path}: not removed, because it holds a link to another folder.", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task AFolderThatIsNowRefused_IsNotRemoved()
    {
        // The finder's rules run again at removal: a folder that moved under Documents is no longer a leftover.
        var path = TempLeftoverEnvironment.Folder(Path.Combine(_env.Documents, "Acme Notes"));

        var result = await Service().RemoveAsync([Folder(path)]);

        Assert.Empty(_recycled);
        Assert.Equal($"{path}: not removed, because it is in Documents, which SysManager never offers.", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task AFolderAlreadyGone_CountsAsRemoved_ButFreesNothing()
    {
        var item = Folder(Path.Combine(_env.RoamingAppData, "Gone"), size: 500);

        var result = await Service().RemoveAsync([item]);

        Assert.Empty(_recycled);
        Assert.Same(item, Assert.Single(result.Removed));
        Assert.Equal(0, result.BytesFreed);
    }

    [Fact]
    public async Task AFolderInsideAnotherRemovedFolder_IsCountedOnce()
    {
        var outer = TempLeftoverEnvironment.Folder(Path.Combine(_env.LocalAppData, "Discord"));
        var inner = TempLeftoverEnvironment.Folder(Path.Combine(outer, "app-1.0"));

        var result = await Service().RemoveAsync([Folder(inner, size: 60), Folder(outer, LeftoverConfidence.Probably, size: 100)]);

        Assert.Equal(2, result.Removed.Count);
        Assert.Equal(100, result.BytesFreed);
    }

    [Fact]
    public async Task WithoutTheRemovalsSupplied_NothingIsRemoved()
    {
        // The seams default to refusing, so a test that forgets them cannot reach the real Recycle Bin or registry.
        var path = TempLeftoverEnvironment.Folder(Path.Combine(_env.RoamingAppData, "Acme Notes"));
        _env.RegistryKeys.Add(@"Software\Acme\Notes");
        RegExport(0, "Windows Registry Editor Version 5.00");

        var result = await new LeftoverService(_runner, _env).RemoveAsync([Folder(path), Key(@"Software\Acme\Notes")]);

        Assert.Empty(result.Removed);
        Assert.Equal(2, result.Errors.Count);
        Assert.True(Directory.Exists(path));
    }

    // ── Keys ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AKey_IsExportedFirst_ThenDeleted()
    {
        _env.RegistryKeys.Add(@"Software\Acme\Notes");
        RegExport(0, "Windows Registry Editor Version 5.00");
        var service = Service();

        var result = await service.RemoveAsync([Key(@"Software\Acme\Notes")]);

        Assert.Equal([@"Software\Acme\Notes"], _deletedKeys);
        Assert.Single(result.Removed);
        var backup = Assert.Single(Directory.GetFiles(service.BackupDirectory, "*.reg"));
        await _runner.Received(1).RunProcessAsync("reg.exe",
            $"export \"HKCU\\Software\\Acme\\Notes\" \"{backup}\" /y", Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    [Theory]
    [InlineData(1, "Windows Registry Editor Version 5.00")]
    [InlineData(0, null)]
    [InlineData(0, "")]
    public async Task AKeyWhoseExportFailed_IsNeverDeleted(int exitCode, string? content)
    {
        _env.RegistryKeys.Add(@"Software\Acme\Notes");
        RegExport(exitCode, content);

        var result = await Service().RemoveAsync([Key(@"Software\Acme\Notes")]);

        Assert.Empty(_deletedKeys);
        Assert.Empty(result.Removed);
        Assert.Equal(@"HKEY_CURRENT_USER\Software\Acme\Notes: not removed, because its backup copy could not be saved.",
            Assert.Single(result.Errors));
    }

    [Fact]
    public async Task AKeyWithAQuoteInItsName_IsNeverHandedToRegExe()
    {
        var hostile = @"Software\Acme\Notes"" ""C:\out.reg";
        _env.RegistryKeys.Add(hostile);

        var result = await Service().RemoveAsync([Key(hostile)]);

        Assert.Empty(_deletedKeys);
        await _runner.DidNotReceiveWithAnyArgs().RunProcessAsync(default!, default!, default, default);
        Assert.Contains("cannot be passed safely to reg.exe", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKeyAlreadyGone_CountsAsRemoved_WithoutAnExport()
    {
        var result = await Service().RemoveAsync([Key(@"Software\Acme\Notes")]);

        Assert.Single(result.Removed);
        Assert.Empty(_deletedKeys);
        await _runner.DidNotReceiveWithAnyArgs().RunProcessAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task AKeyWindowsWouldNotDelete_SaysWhereItsBackupIs()
    {
        _env.RegistryKeys.Add(@"Software\Acme\Notes");
        RegExport(0, "Windows Registry Editor Version 5.00");
        var service = Service(deleteWorks: false);

        var result = await service.RemoveAsync([Key(@"Software\Acme\Notes")]);

        Assert.Empty(result.Removed);
        var backup = Assert.Single(Directory.GetFiles(service.BackupDirectory, "*.reg"));
        Assert.Equal($@"HKEY_CURRENT_USER\Software\Acme\Notes: Windows did not let SysManager delete it. Its backup is in {backup}.",
            Assert.Single(result.Errors));
    }

    // ── What waits for administrator rights ───────────────────────────────

    [Fact]
    public void OnlyWhatNeedsAdministratorRights_IsRemembered_AndOfferedAgainAsItWas()
    {
        var install = TempLeftoverEnvironment.Folder(Path.Combine(_env.ProgramFiles, "Acme Notes"), bytes: 7);
        var guess = TempLeftoverEnvironment.Folder(Path.Combine(_env.ProgramData, "Acme"));
        var roaming = TempLeftoverEnvironment.Folder(Path.Combine(_env.RoamingAppData, "Acme Notes"));
        var group = new LeftoverGroup
        {
            AppName = "Acme Notes",
            Publisher = "Acme",
            Items =
            [
                Folder(install, needsAdmin: true),
                new LeftoverItem { Location = guess, Kind = LeftoverKind.Folder, Confidence = LeftoverConfidence.Guess, Publisher = "Acme", NeedsAdministrator = true },
                Folder(roaming, LeftoverConfidence.Probably),
                Key(@"Software\Acme\Notes"),
            ],
        };
        var service = Service();

        service.RememberPending([group]);
        _env.IsElevated = true;
        var pending = Assert.Single(service.LoadPending());

        Assert.Equal("Acme Notes", pending.AppName);
        Assert.Equal([install, guess], pending.Items.Select(i => i.Location));
        // Unticked even though Certain: the record is in the user's own profile, and anything running as the user could
        // have rewritten it before this session with administrator rights read it.
        var certain = pending.Items[0];
        Assert.Equal((LeftoverConfidence.Certain, false, false, 7L), (certain.Confidence, certain.IsSelected, certain.NeedsAdministrator, certain.SizeBytes));
        var remembered = pending.Items[1];
        Assert.Equal((LeftoverConfidence.Guess, "Acme", false), (remembered.Confidence, remembered.Publisher, remembered.IsSelected));
    }

    [Fact]
    public void AFolderGoneOrNowInUse_IsDroppedFromTheRecord()
    {
        var gone = TempLeftoverEnvironment.Folder(Path.Combine(_env.ProgramFiles, "Gone"));
        var reused = TempLeftoverEnvironment.Folder(Path.Combine(_env.ProgramFiles, "Reused"));
        var kept = TempLeftoverEnvironment.Folder(Path.Combine(_env.ProgramFiles, "Kept"));
        var service = Service();
        service.RememberPending([new LeftoverGroup
        {
            AppName = "Acme Notes",
            Items = [Folder(gone, needsAdmin: true), Folder(reused, needsAdmin: true), Folder(kept, needsAdmin: true)],
        }]);
        Directory.Delete(gone);
        _env.Registered.Add(new UninstallProbe("Something New", "", "", reused));
        _env.IsElevated = true;

        Assert.Equal([kept], Assert.Single(service.LoadPending()).Items.Select(i => i.Location));
        var record = LeftoverService.Parse(File.ReadAllText(service.PendingPath));
        Assert.Equal([kept], Assert.Single(record).Folders.Select(f => f.Path));
    }

    [Fact]
    public void ForgettingEveryFolder_RemovesTheRecord()
    {
        var install = TempLeftoverEnvironment.Folder(Path.Combine(_env.ProgramFiles, "Acme Notes"));
        var service = Service();
        service.RememberPending([new LeftoverGroup { AppName = "Acme Notes", Items = [Folder(install, needsAdmin: true)] }]);
        Assert.True(File.Exists(service.PendingPath));

        service.ForgetPending([install.ToUpperInvariant()]);

        Assert.False(File.Exists(service.PendingPath));
        Assert.Empty(service.LoadPending());
    }

    [Fact]
    public void RememberingTheSameFolderTwice_KeepsOneEntry()
    {
        var install = TempLeftoverEnvironment.Folder(Path.Combine(_env.ProgramFiles, "Acme Notes"));
        var service = Service();
        var group = new LeftoverGroup { AppName = "Acme Notes", Items = [Folder(install, needsAdmin: true)] };

        service.RememberPending([group]);
        service.RememberPending([group]);

        Assert.Single(Assert.Single(LeftoverService.Parse(File.ReadAllText(service.PendingPath))).Folders);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""[{"AppName":"A","Publisher":"","Folders":[{"Path":"C:\\X","Confidence":"Sure","Publisher":""}]}]""")]
    public void AnUnreadableRecord_OffersNothing(string? json)
        => Assert.Empty(LeftoverService.Parse(json));

    [Fact]
    public void ARecordWithEmptyEntries_KeepsOnlyWhatNamesAFolder()
    {
        const string json = """
            [
              {"AppName":"","Folders":[{"Path":"C:\\A","Confidence":"Certain"}]},
              {"AppName":"B","Folders":[null,{"Path":"","Confidence":"Certain"},{"Path":"C:\\B","Confidence":"Guess"}]},
              {"AppName":"C","Folders":[]}
            ]
            """;

        var group = Assert.Single(LeftoverService.Parse(json));

        Assert.Equal("B", group.AppName);
        Assert.Equal("", group.Publisher);
        var folder = Assert.Single(group.Folders);
        Assert.Equal((@"C:\B", LeftoverConfidence.Guess, ""), (folder.Path, folder.Confidence, folder.Publisher));
    }
}
