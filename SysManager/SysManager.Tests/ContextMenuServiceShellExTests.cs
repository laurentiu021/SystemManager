// SysManager · ContextMenuServiceShellExTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Microsoft.Win32;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for the COM shell-extension half of <see cref="ContextMenuService.ScanEntries"/> (#1510).
/// </summary>
/// <remarks>
/// Before this, the scan read four <c>shell</c> verb roots and nothing else, so the tab showed the small
/// tidy half of the right-click menu and hid the half that makes it slow — every handler a program
/// registers under <c>shellex\ContextMenuHandlers</c>.
/// <para>The service now takes an injectable stand-in for <c>HKEY_CLASSES_ROOT</c>, which is what makes
/// any of this assertable: pointed at the real hive, a test could only claim whatever the machine running
/// it happened to have installed. Same seam and same reason as <c>AppBlockerServiceRegistryTests</c>.</para>
/// </remarks>
public sealed class ContextMenuServiceShellExTests : IDisposable
{
    private const string Archiver = "{11111111-2222-3333-4444-555555555555}";
    private const string Sync = "{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}";
    private const string Orphan = "{99999999-8888-7777-6666-555555555555}";

    private const string BlockedListPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    private readonly string _treeName = @"Software\SysManagerTests\ContextMenu_" + Guid.NewGuid().ToString("N");

    /// <summary>Stands in for HKEY_CLASSES_ROOT.</summary>
    private readonly RegistryKey _root;

    /// <summary>
    /// Stands in for HKEY_LOCAL_MACHINE, which is where the shell-extension Blocked list lives.
    /// </summary>
    /// <remarks>
    /// Without this seam the block/unblock tests would have to write the real
    /// <c>HKLM\…\Shell Extensions\Blocked</c> — needing administrator rights, and changing which add-ons
    /// load on the machine running the suite, to prove something about a registry value. Both stand-ins
    /// live under one disposable tree so a single delete cleans up.
    /// </remarks>
    private readonly RegistryKey _machine;

    // A class name unique to this test run, used by the tests that assert LegacyDisable is NOT the
    // mechanism for a handler. If that ever regresses, the HKCU fallback writes under
    // Software\Classes\<this>, which Dispose then removes — so a regression is caught by an assertion
    // rather than by littering the developer's registry.
    private readonly string _refusalClass = "SysManagerTests_Handler_" + Guid.NewGuid().ToString("N");

    public ContextMenuServiceShellExTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_treeName + @"\Classes", writable: true)!;
        _machine = Registry.CurrentUser.CreateSubKey(_treeName + @"\Machine", writable: true)!;
    }

    public void Dispose()
    {
        _root.Dispose();
        _machine.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_treeName, throwOnMissingSubKey: false); } catch { /* best-effort cleanup */ }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + _refusalClass, throwOnMissingSubKey: false); } catch { /* best-effort cleanup */ }
    }

    /// <summary>Puts a CLSID on the fake machine's Blocked list, the way blocking one does.</summary>
    private void GivenBlocked(string clsid, string name = "Already blocked")
    {
        using var key = _machine.CreateSubKey(BlockedListPath, writable: true)!;
        key.SetValue(clsid, name, RegistryValueKind.String);
    }

    /// <summary>The Blocked-list value for a CLSID, or null when it is not blocked.</summary>
    private object? BlockedValue(string clsid)
    {
        using var key = _machine.OpenSubKey(BlockedListPath);
        return key?.GetValue(clsid);
    }

    /// <summary>Registers a handler under one shellex root, the way a real installer does.</summary>
    private void GivenHandler(string shellExRoot, string keyName, string? clsidValue)
    {
        using var key = _root.CreateSubKey($@"{shellExRoot}\shellex\ContextMenuHandlers\{keyName}", writable: true)!;
        if (clsidValue is not null) key.SetValue("", clsidValue);
    }

    /// <summary>Registers the COM class a handler points at, so its name and DLL can be resolved.</summary>
    private void GivenComClass(string clsid, string friendlyName, string? inprocDll = null, string? localServer = null)
    {
        using var key = _root.CreateSubKey($@"CLSID\{clsid}", writable: true)!;
        key.SetValue("", friendlyName);
        if (inprocDll is not null)
            using (var s = key.CreateSubKey("InprocServer32", writable: true)!) s.SetValue("", inprocDll);
        if (localServer is not null)
            using (var s = key.CreateSubKey("LocalServer32", writable: true)!) s.SetValue("", localServer);
    }

    /// <summary>Registers a classic verb, so the two shapes can be asserted side by side.</summary>
    private void GivenVerb(string classRoot, string verb, string command)
    {
        using var key = _root.CreateSubKey($@"{classRoot}\shell\{verb}", writable: true)!;
        using var cmd = key.CreateSubKey("command", writable: true)!;
        cmd.SetValue("", command);
    }

    // Both hives are the fakes, so a scan asserts what the test set up and nothing the developer's
    // machine happens to have blocked.
    private ContextMenuService Service() => new(_root, _machine);

    private List<ContextMenuEntry> Scan() => Service().ScanEntries();

    [Fact]
    public void ScanEntries_ReadsAHandlerAndResolvesItsNameAndDll()
    {
        GivenHandler("*", "TestArchiver", Archiver);
        GivenComClass(Archiver, "Test Archiver Shell Extension", @"C:\Program Files\TestArchiver\ext.dll");

        var entry = Assert.Single(Scan());

        Assert.Equal(ContextMenuEntryKind.Handler, entry.Kind);
        Assert.Equal("Handler", entry.KindLabel);
        Assert.Equal(Archiver, entry.Clsid);
        Assert.Equal("Test Archiver Shell Extension", entry.Name);
        Assert.Equal(@"C:\Program Files\TestArchiver\ext.dll", entry.Command);
        Assert.Equal("Files", entry.Location);
        Assert.Equal("TestArchiver", entry.RawName);
        Assert.Equal(@"HKCR\*\shellex\ContextMenuHandlers\TestArchiver", entry.RegistryPath);
    }

    [Fact]
    public void ScanEntries_HandlerSource_IsNotTruncatedAtTheFirstSpace()
    {
        // A verb's registration is a command line, so ExtractSource stops at the first space to drop the
        // arguments. A handler's is a bare path, and most handlers install under "Program Files" — through
        // the command-line parser every one of them read "Program".
        GivenHandler("*", "TestArchiver", Archiver);
        GivenComClass(Archiver, "Test Archiver Shell Extension", @"C:\Program Files\TestArchiver\vendor-ext.dll");

        Assert.Equal("vendor-ext", Assert.Single(Scan()).Source);
    }

    [Fact]
    public void ScanEntries_ExpandsAnEnvironmentVariableInAHandlerPath()
    {
        // The handlers built into Windows register as %SystemRoot%\system32\…, which matches no file until
        // it is expanded — so the row fell back to the bare file name instead of naming the program behind
        // it. Asserted as "not the bare file name" rather than against a product string, which is localised.
        GivenHandler("*", "TestSystemHandler", Archiver);
        GivenComClass(Archiver, "Test System Handler", @"%SystemRoot%\system32\shell32.dll");

        var source = Assert.Single(Scan()).Source;

        Assert.NotEqual("", source);
        Assert.NotEqual("shell32", source);
    }

    [Fact]
    public void ScanEntries_FallsBackToTheKeyNameWhenItIsItselfTheClsid()
    {
        // Plenty of installers use the CLSID as the key name and leave the default value empty.
        GivenHandler("Directory", Sync, clsidValue: null);
        GivenComClass(Sync, "Test Sync Overlay", @"%SystemRoot%\system32\nonexistent-test.dll");

        var entry = Assert.Single(Scan());

        Assert.Equal(Sync, entry.Clsid);
        Assert.Equal("Test Sync Overlay", entry.Name);
        Assert.Equal("Folders", entry.Location);
    }

    [Fact]
    public void ScanEntries_KeepsAHandlerWhoseComClassIsGone()
    {
        // The leftover case, and one of the reasons to show this list at all: an uninstaller that removed
        // its DLL and its CLSID registration but left the ContextMenuHandlers entry behind. Explorer still
        // looks for it on every right-click.
        GivenHandler("Directory", "DeadVendor", Orphan);

        var entry = Assert.Single(Scan());

        Assert.Equal(Orphan, entry.Clsid);
        Assert.Equal("DeadVendor", entry.Name);   // no friendly name to resolve, so the key name stands in
        Assert.Equal("", entry.Command);
        Assert.Contains("no longer installed", entry.Explanation);
    }

    [Fact]
    public void ScanEntries_NamesAnOrphanByItsClsid_WhenTheKeyNameIsAlsoTheClsid()
    {
        // Nothing readable exists anywhere: no friendly name, and the key name is the GUID. Showing the
        // GUID is right — it is the only identifier there is, and it is what a search engine takes.
        GivenHandler("Directory", Orphan, clsidValue: null);

        Assert.Equal(Orphan, Assert.Single(Scan()).Name);
    }

    [Theory]
    [InlineData("NotAGuidAtAll")]                                  // free text, in both places
    [InlineData("11111111-2222-3333-4444-555555555555")]           // a GUID with the braces missing
    [InlineData("{11111111-2222-3333-4444-55555555555}")]           // one hex digit short
    [InlineData("{11111111-2222-3333-4444-555555555555} ; extra")] // a CLSID with something appended
    public void ScanEntries_SkipsAHandlerWithNoUsableClsid(string junk)
    {
        // HKCR merges HKCU\Software\Classes, so a handler key name is influenced by an unprivileged user.
        // Anything that is not exactly a CLSID is dropped rather than shown as one.
        GivenHandler("*", junk, junk);

        Assert.Empty(Scan());
    }

    [Fact]
    public void ScanEntries_DeDupesOneHandlerRegisteredUnderTwoRootsThatShareALocation()
    {
        // Directory and Folder both present as "Folders", and archive and sync tools routinely register
        // under both. Listing the same add-on twice would make the tab look worse than the problem it is
        // describing.
        GivenHandler("Directory", "TestArchiver", Archiver);
        GivenHandler("Folder", "TestArchiver", Archiver);
        GivenComClass(Archiver, "Test Archiver Shell Extension");

        var entry = Assert.Single(Scan());
        Assert.Equal("Folders", entry.Location);
    }

    [Fact]
    public void ScanEntries_KeepsOneHandlerPerDistinctLocation()
    {
        // The mirror of the de-dupe test: same CLSID, genuinely different places it applies. Collapsing
        // these would hide that the add-on is on files as well as folders.
        GivenHandler("*", "TestArchiver", Archiver);
        GivenHandler("Directory", "TestArchiver", Archiver);
        GivenComClass(Archiver, "Test Archiver Shell Extension");

        var locations = Scan().Select(e => e.Location).OrderBy(l => l, StringComparer.Ordinal).ToList();
        Assert.Equal(["Files", "Folders"], locations);
    }

    [Fact]
    public void ScanEntries_ReturnsVerbsAndHandlersInOneListEachSayingWhichItIs()
    {
        GivenVerb("*", "TestOpenWith", @"""C:\TestVendor\app.exe"" ""%1""");
        GivenHandler("*", "TestArchiver", Archiver);
        GivenComClass(Archiver, "Test Archiver Shell Extension");

        var entries = Scan();

        var verb = Assert.Single(entries, e => e.Kind == ContextMenuEntryKind.MenuEntry);
        Assert.Equal("Menu entry", verb.KindLabel);
        Assert.False(verb.RequiresElevation, "A verb falls back to an HKCU override, so it works unelevated.");
        Assert.Equal("", verb.Clsid);

        var handler = Assert.Single(entries, e => e.Kind == ContextMenuEntryKind.Handler);
        Assert.True(handler.RequiresElevation, "Blocking a handler writes HKLM, so the row has to say it needs admin.");
    }

    // ---- The read side of the Blocked list: a blocked add-on has to show as off ----

    [Fact]
    public void ScanEntries_ReportsABlockedHandlerAsDisabled()
    {
        // Without this the tab shows every add-on as active, so a user who blocked one — here, or in
        // Autoruns — is told it is still running and has no way to undo it from this list.
        GivenHandler("*", "TestArchiver", Archiver);
        GivenComClass(Archiver, "Test Archiver Shell Extension", @"C:\Program Files\TestArchiver\ext.dll");
        GivenBlocked(Archiver);

        Assert.False(Assert.Single(Scan()).IsEnabled);
    }

    [Fact]
    public void ScanEntries_ReportsAHandlerThatIsNotOnTheListAsEnabled()
    {
        GivenHandler("*", "TestArchiver", Archiver);
        GivenComClass(Archiver, "Test Archiver Shell Extension", @"C:\Program Files\TestArchiver\ext.dll");
        GivenBlocked(Sync);   // a different add-on is blocked

        Assert.True(Assert.Single(Scan()).IsEnabled);
    }

    [Fact]
    public void ScanEntries_MatchesTheBlockedListRegardlessOfCase()
    {
        // Registry value names are case-insensitive and installers are inconsistent about GUID casing, so
        // a case-sensitive comparison would report a blocked add-on as active roughly half the time.
        GivenHandler("*", "TestArchiver", Archiver.ToUpperInvariant());
        GivenComClass(Archiver.ToUpperInvariant(), "Test Archiver Shell Extension");
        GivenBlocked(Archiver.ToLowerInvariant());

        Assert.False(Assert.Single(Scan()).IsEnabled);
    }

    [Fact]
    public void ReadBlockedClsids_IgnoresAValueNameThatIsNotAClsid()
    {
        using (var key = _machine.CreateSubKey(BlockedListPath, writable: true)!)
        {
            key.SetValue(Archiver, "A real one", RegistryValueKind.String);
            key.SetValue("NotAClsid", "junk", RegistryValueKind.String);
        }

        Assert.Equal([Archiver], Service().ReadBlockedClsids());
    }

    [Fact]
    public void ReadBlockedClsids_IsEmptyWhenNothingHasEverBeenBlocked()
    {
        // The key is absent on most machines. That is the normal case, not a failure to read.
        Assert.Empty(Service().ReadBlockedClsids());
    }

    // ---- Windows' own handlers belong behind the "show system entries" filter ----

    // Derived rather than written as "C:\Windows": the directory is not guaranteed to be there, and the
    // sibling case below has to be a real sibling of whatever it actually is to test anything.
    private static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    [Fact]
    public void ScanEntries_TreatsAHandlerInsideWindowsAsASystemEntry()
    {
        // Part of the shell rather than something a program added, so it belongs off-screen by default and
        // out of every preset's reach — the same treatment the internal verbs get.
        GivenHandler("*", "TestSystemHandler", Archiver);
        GivenComClass(Archiver, "Test System Handler", Path.Combine(WindowsDir, "system32", "shell32.dll"));

        Assert.True(Assert.Single(Scan()).IsSystemEntry);
    }

    [Fact]
    public void ScanEntries_TreatsAnUnexpandedWindowsPathAsASystemEntry()
    {
        // How the built-in handlers actually register. Compared before expansion, %SystemRoot% matches
        // nothing and every one of them reads as third-party — visible by default and inside a preset's
        // reach, which is the wrong answer twice.
        GivenHandler("*", "TestSystemHandler", Archiver);
        GivenComClass(Archiver, "Test System Handler", @"%SystemRoot%\system32\shell32.dll");

        Assert.True(Assert.Single(Scan()).IsSystemEntry);
    }

    [Fact]
    public void ScanEntries_DoesNotMistakeASiblingOfTheWindowsDirectoryForIt()
    {
        // A prefix comparison without a separator sweeps in C:\Windows-Anything, which would hide a
        // third-party add-on behind the system filter and make it untouchable by a preset.
        GivenHandler("*", "TestArchiver", Archiver);
        GivenComClass(Archiver, "Test Archiver Shell Extension", WindowsDir + "-NotReally\\ext.dll");

        Assert.False(Assert.Single(Scan()).IsSystemEntry);
    }

    [Theory]
    [InlineData(@"C:\Program Files\TestArchiver\ext.dll")]
    [InlineData("")]   // an orphan, whose DLL is gone
    public void ScanEntries_TreatsAHandlerOutsideWindowsAsThirdParty(string dll)
    {
        GivenHandler("*", "TestArchiver", Archiver);
        GivenComClass(Archiver, "Test Archiver Shell Extension", dll.Length == 0 ? null : dll);

        Assert.False(Assert.Single(Scan()).IsSystemEntry);
    }

    [Fact]
    public void ResolveClsid_PrefersInprocServer32_AndFallsBackToLocalServer32()
    {
        GivenComClass(Archiver, "Both Servers", inprocDll: @"C:\in\proc.dll", localServer: @"C:\out\proc.exe");
        GivenComClass(Sync, "Out Of Process Only", localServer: @"C:\out\only.exe");

        var svc = new ContextMenuService(_root);

        Assert.Equal(@"C:\in\proc.dll", svc.ResolveClsid(Archiver).Dll);
        Assert.Equal(@"C:\out\only.exe", svc.ResolveClsid(Sync).Dll);
        Assert.Equal(("", ""), svc.ResolveClsid(Orphan));
    }

    [Fact]
    public void ResolveClsid_StripsTheQuotesAroundAServerPath()
    {
        GivenComClass(Archiver, "Quoted", inprocDll: @"""C:\Program Files\TestArchiver\ext.dll""");

        Assert.Equal(@"C:\Program Files\TestArchiver\ext.dll", new ContextMenuService(_root).ResolveClsid(Archiver).Dll);
    }

    // ---- The write side: a handler is hidden through the Blocked list, never through LegacyDisable ----

    private ContextMenuEntry AHandlerRow(string? clsid = null) => new()
    {
        Kind = ContextMenuEntryKind.Handler,
        Clsid = clsid ?? Archiver,
        Name = "Test Archiver Shell Extension",
        Command = @"C:\Program Files\TestArchiver\ext.dll",
        RegistryPath = $@"HKCR\{_refusalClass}\shellex\ContextMenuHandlers\TestArchiver",
        Location = "Files",
        IsEnabled = true
    };

    [Fact]
    public void DisableEntry_BlocksAHandlerByClsid_AndNeverWritesLegacyDisable()
    {
        // LegacyDisable on a shellex key SUCCEEDS and does nothing: Explorer only honours it on a verb.
        // So the disable path has to go somewhere else entirely, and the two halves of that are asserted
        // together — the CLSID reaches the Blocked list, and no value is left on the handler's own key.
        var entry = AHandlerRow();

        Assert.True(Service().DisableEntry(entry));
        Assert.False(entry.IsEnabled);
        Assert.Equal("Test Archiver Shell Extension", BlockedValue(Archiver));
        AssertNoFallbackKeyWasCreated();
    }

    [Fact]
    public void DisableEntry_CreatesTheBlockedList_WhenNothingHasEverBeenBlocked()
    {
        // The common case on a real machine: the key does not exist at all. A disable that quietly did
        // nothing here would be the whole feature failing on exactly the machines that need it.
        Assert.Null(_machine.OpenSubKey(BlockedListPath));

        Assert.True(Service().DisableEntry(AHandlerRow()));

        Assert.NotNull(BlockedValue(Archiver));
    }

    [Fact]
    public void EnableEntry_RemovesTheClsidFromTheBlockedList()
    {
        GivenBlocked(Archiver);
        var entry = AHandlerRow();
        entry.IsEnabled = false;

        Assert.True(Service().EnableEntry(entry));

        Assert.True(entry.IsEnabled);
        Assert.Null(BlockedValue(Archiver));
    }

    [Fact]
    public void EnableEntry_LeavesOtherBlockedHandlersAlone()
    {
        // Deleting one value, not clearing the list. Worth pinning because the list is machine-wide and
        // shared with anything else that blocks handlers, so over-deleting would silently re-enable an
        // add-on the user blocked somewhere else.
        GivenBlocked(Archiver);
        GivenBlocked(Sync, "Someone else's choice");

        Assert.True(Service().EnableEntry(AHandlerRow()));

        Assert.Null(BlockedValue(Archiver));
        Assert.Equal("Someone else's choice", BlockedValue(Sync));
    }

    [Fact]
    public void EnableEntry_SucceedsWhenTheHandlerWasNotBlocked()
    {
        // Asking for a state that is already true is not a failure — reporting one would put the switch
        // back and tell the user something went wrong when the add-on is exactly as they asked.
        var entry = AHandlerRow();
        entry.IsEnabled = false;

        Assert.True(Service().EnableEntry(entry));
        Assert.True(entry.IsEnabled);
    }

    [Fact]
    public void EnableEntry_DoesNotDeleteLegacyDisableFromAHandlersOwnKey()
    {
        // The mirror of the disable assertion. The verb path DELETES a value, so against a key that does
        // not exist it does nothing whether or not the routing is right — an assertion on a missing key
        // would pass either way. So the value it would delete is put there first: surviving the call is
        // the evidence that the handler went down the Blocked-list path instead.
        var handlerKeyPath = $@"Software\Classes\{_refusalClass}\shellex\ContextMenuHandlers\TestArchiver";
        using (var seeded = Registry.CurrentUser.CreateSubKey(handlerKeyPath, writable: true)!)
            seeded.SetValue("LegacyDisable", "", RegistryValueKind.String);

        var entry = AHandlerRow();
        entry.IsEnabled = false;

        Assert.True(Service().EnableEntry(entry));

        using var after = Registry.CurrentUser.OpenSubKey(handlerKeyPath);
        Assert.NotNull(after);
        Assert.NotNull(after.GetValue("LegacyDisable"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("NotAGuidAtAll")]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    [InlineData(@"{11111111-2222-3333-4444-555555555555}\..\..\Run")]
    public void DisableEntry_RefusesAHandlerWhoseClsidIsNotOne_AndWritesNothing(string junk)
    {
        // SEC: the CLSID becomes a registry VALUE NAME under HKLM, and it was enumerated out of HKCR,
        // which merges the user-writable HKCU\Software\Classes. The scan drops non-CLSIDs already; this is
        // the check standing next to the write, which is the one that usually runs elevated.
        var entry = AHandlerRow(junk);

        Assert.False(Service().DisableEntry(entry));
        Assert.True(entry.IsEnabled, "A refused disable must leave the row's state alone.");
        Assert.Null(_machine.OpenSubKey(BlockedListPath));
    }

    /// <summary>
    /// Proves nothing was written to the handler's own key, not merely that the call reported success.
    /// </summary>
    /// <remarks>
    /// The verb path writes through <c>Registry.ClassesRoot</c>, not the injected root, and falls back to
    /// <c>HKCU\Software\Classes</c> when HKCR is not writable — which it is not for a class that does not
    /// exist. So a handler wrongly sent down that path leaves a key behind under a name unique to this
    /// test run, and that key's absence is the assertion.
    /// </remarks>
    private void AssertNoFallbackKeyWasCreated()
    {
        using var leftover = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + _refusalClass);
        Assert.Null(leftover);
    }

    // ---- Every location the scan emits has to be reachable from the filter ----

    [Fact]
    public async Task EveryScannedLocation_HasAFilterThatMatchesIt()
    {
        // A row whose Location is missing from the ComboBox appears under "All" and then vanishes for
        // every specific choice, which reads as the filter being broken rather than as a gap in a list.
        // AllFilesystemObjects is the one that exposed this: it emits "Files and folders", which the
        // original five-item list did not carry.
        //
        // Driven through the real scan rather than a copy of the location table, so a root added to the
        // service with a new label fails here instead of shipping unreachable.
        foreach (var classRoot in (string[])["*", "Directory", @"Directory\Background", "DesktopBackground", "Folder", "AllFilesystemObjects"])
        {
            GivenHandler(classRoot, Archiver, clsidValue: null);
            GivenVerb(classRoot, "TestVerb", @"C:\TestVendor\app.exe");
        }
        GivenComClass(Archiver, "Test Archiver Shell Extension");

        var scanned = Scan().Select(e => e.Location).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var service = Substitute.For<IContextMenuService>();
        service.ScanEntries().Returns([]);
        using var vm = new ContextMenuViewModel(service);
        await vm.InitializationComplete;

        Assert.True(scanned.Count >= 5,
            $"Expected the scan to emit at least 5 distinct locations, got {scanned.Count} — the fake hive "
            + "is probably not being read, which would make this pass while checking nothing.");

        var unreachable = scanned.Where(l => !vm.LocationFilters.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.True(unreachable.Count == 0,
            "These locations can appear on a row but no filter selects them, so those rows are only ever "
            + "visible under \"All\". Add them to ContextMenuViewModel.LocationFilters: "
            + string.Join(", ", unreachable));
    }
}
