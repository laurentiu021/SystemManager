// SysManager · InstalledProgramsHistoryTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// The list of installed programs Recent Changes keeps from one look to the next (#1507), in a temp folder and over a
/// list of programs the test sets, so nothing here reads this PC's programs or writes to the user's profile.
/// </summary>
public sealed class InstalledProgramsHistoryTests : IDisposable
{
    private static readonly DateTime FirstLook = new(2026, 10, 2, 9, 0, 0);
    private static readonly DateTime SecondLook = new(2026, 10, 5, 15, 20, 0);

    private readonly string _dir;
    private UninstallList _installed = new([], Complete: true);

    public InstalledProgramsHistoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { /* already gone */ }
    }

    private InstalledProgramsHistory NewHistory() => new(_dir, () => _installed);

    private string StoreFile => Path.Combine(_dir, "installed-programs.json");

    private static UninstallEntry Program(string name, string publisher = "Contoso", bool hidden = false, bool update = false) =>
        new(name, publisher, "", "", IsSystemComponent: hidden, IsUpdate: update);

    private void Installed(params UninstallEntry[] programs) => _installed = new(programs, Complete: true);

    // ── What a look finds ──────────────────────────────────────────────────

    [Fact]
    public void TheFirstLook_OnlyStartsTheRecord()
    {
        Installed(Program("Contoso Notes"), Program("Contoso Paint"));

        var look = NewHistory().Observe(FirstLook);

        Assert.True(look.Readable);
        Assert.True(look.FirstLook);
        Assert.Empty(look.Changes);
        Assert.True(File.Exists(StoreFile));
    }

    [Fact]
    public void AProgramThatAppears_IsReportedWithTheTimeBetweenTheTwoLooks()
    {
        Installed(Program("Contoso Notes"));
        NewHistory().Observe(FirstLook);
        Installed(Program("Contoso Notes"), Program("Search Pro Toolbar", "Fabrikam"));

        var look = NewHistory().Observe(SecondLook);

        Assert.False(look.FirstLook);
        Assert.Equal(new ProgramChange("Search Pro Toolbar", "Fabrikam", ChangeKind.ProgramAppeared, FirstLook, SecondLook),
            Assert.Single(look.Changes));
    }

    [Fact]
    public void AProgramThatLeaves_IsReportedGone()
    {
        Installed(Program("Contoso Notes"), Program("Contoso Paint"));
        NewHistory().Observe(FirstLook);
        Installed(Program("Contoso Notes"));

        var change = Assert.Single(NewHistory().Observe(SecondLook).Changes);

        Assert.Equal(ChangeKind.ProgramDisappeared, change.Kind);
        Assert.Equal("Contoso Paint", change.Name);
    }

    [Fact]
    public void ANewVersionOfTheSameProgram_IsAnUpdate_NotOneRemovedAndAnotherInstalled()
    {
        Installed(Program("Python 3.12.1 (64-bit)", "Python Software Foundation"));
        NewHistory().Observe(FirstLook);
        Installed(Program("Python 3.12.2 (64-bit)", "Python Software Foundation"));

        var change = Assert.Single(NewHistory().Observe(SecondLook).Changes);

        Assert.Equal(new ProgramChange("Python 3.12.2 (64-bit)", "Python Software Foundation", ChangeKind.ProgramUpdated,
            FirstLook, SecondLook, Before: "Python 3.12.1 (64-bit)"), change);
    }

    [Fact]
    public void WithNoPublisher_ARenameIsNotTakenForAnUpdate()
    {
        // Nothing but the name to go on, and two programs can share a name once their versions are taken out.
        Installed(Program("Notes 1.0", publisher: ""));
        NewHistory().Observe(FirstLook);
        Installed(Program("Notes 2.0", publisher: ""));

        var kinds = NewHistory().Observe(SecondLook).Changes.Select(c => c.Kind).ToList();

        Assert.Equal([ChangeKind.ProgramAppeared, ChangeKind.ProgramDisappeared], kinds);
    }

    [Fact]
    public void HiddenComponentsAndUpdates_AreNotPrograms()
    {
        // They come and go with the programs they belong to; Windows' own list does not show them either.
        Installed(Program("Contoso Notes"));
        NewHistory().Observe(FirstLook);
        Installed(Program("Contoso Notes"), Program("Contoso Notes Helper", hidden: true), Program("Contoso Notes KB12", update: true),
            Program(""));

        Assert.Empty(NewHistory().Observe(SecondLook).Changes);
    }

    [Fact]
    public void AProgramListedTwice_IsOneProgram()
    {
        // Listed for the machine and for the user, in different letter case.
        Installed(Program("Contoso Notes"));
        NewHistory().Observe(FirstLook);
        Installed(Program("Contoso Notes"), Program("CONTOSO NOTES", "CONTOSO"));

        Assert.Empty(NewHistory().Observe(SecondLook).Changes);
    }

    [Fact]
    public void WhatALookFound_IsStillThereAtTheNext()
    {
        Installed(Program("Contoso Notes"));
        NewHistory().Observe(FirstLook);
        Installed(Program("Contoso Notes"), Program("Contoso Paint"));
        NewHistory().Observe(SecondLook);

        var later = NewHistory().Observe(SecondLook.AddDays(1));

        Assert.Equal("Contoso Paint", Assert.Single(later.Changes).Name);
    }

    [Fact]
    public void ADifferenceOlderThanRecentChangesLooksBack_IsDropped()
    {
        Installed(Program("Contoso Notes"));
        NewHistory().Observe(FirstLook);
        Installed(Program("Contoso Notes"), Program("Contoso Paint"));
        NewHistory().Observe(SecondLook);

        var kept = NewHistory().Observe(SecondLook + RecentChangesService.KeptFor);
        var dropped = NewHistory().Observe(SecondLook + RecentChangesService.KeptFor + TimeSpan.FromSeconds(1));

        Assert.Single(kept.Changes);
        Assert.Empty(dropped.Changes);
    }

    [Fact]
    public void AtMostMaxChangesAreKept_TheNewest()
    {
        NewHistory().Observe(FirstLook);
        Installed([.. Enumerable.Range(0, InstalledProgramsHistory.MaxChanges + 1).Select(i => Program($"Program {i:D3}"))]);

        var changes = NewHistory().Observe(SecondLook).Changes;

        Assert.Equal(InstalledProgramsHistory.MaxChanges, changes.Count);
        Assert.DoesNotContain(changes, c => c.Name == "Program 000");
    }

    // ── When something cannot be read ──────────────────────────────────────

    [Fact]
    public void WhenWindowsListCannotBeReadWhole_NothingIsComparedOrWritten()
    {
        // Compared, the programs missing from a partial list would all be reported removed, and then all installed
        // again at the next look.
        Installed(Program("Contoso Notes"), Program("Contoso Paint"));
        NewHistory().Observe(FirstLook);
        var before = File.ReadAllBytes(StoreFile);
        _installed = new([Program("Contoso Notes")], Complete: false);

        var look = NewHistory().Observe(SecondLook);

        Assert.False(look.Readable);
        Assert.False(look.FirstLook);
        Assert.Empty(look.Changes);
        Assert.Equal(before, File.ReadAllBytes(StoreFile));

        Installed(Program("Contoso Notes"), Program("Contoso Paint"));
        Assert.Empty(NewHistory().Observe(SecondLook.AddHours(1)).Changes);
    }

    [Fact]
    public void WhenWindowsListCannotBeReadWhole_WhatEarlierLooksFoundIsStillGiven()
    {
        Installed(Program("Contoso Notes"));
        NewHistory().Observe(FirstLook);
        Installed(Program("Contoso Notes"), Program("Contoso Paint"));
        NewHistory().Observe(SecondLook);
        _installed = new([], Complete: false);

        var look = NewHistory().Observe(SecondLook.AddDays(1));

        Assert.False(look.Readable);
        Assert.Equal("Contoso Paint", Assert.Single(look.Changes).Name);
    }

    [Fact]
    public void WhenTheRecordCannotBeRead_NothingIsWritten()
    {
        Installed(Program("Contoso Notes"));
        NewHistory().Observe(FirstLook);
        var before = File.ReadAllBytes(StoreFile);
        Installed(Program("Contoso Notes"), Program("Contoso Paint"));

        ProgramsLook look;
        using (new FileStream(StoreFile, FileMode.Open, FileAccess.Read, FileShare.Delete))
            look = NewHistory().Observe(SecondLook);

        Assert.Same(ProgramsLook.Unreadable, look);
        Assert.Equal(before, File.ReadAllBytes(StoreFile));
    }

    [Fact]
    public void ARecordThatDoesNotParse_IsSetAside_AndANewOneStarts()
    {
        File.WriteAllText(StoreFile, "{ not json");
        Installed(Program("Contoso Notes"));

        var look = NewHistory().Observe(FirstLook);

        Assert.True(look.Readable);
        Assert.True(look.FirstLook);
        Assert.Equal("{ not json", File.ReadAllText(StoreFile + ".unreadable"));
        Assert.NotNull(InstalledProgramsHistory.Parse(File.ReadAllText(StoreFile)));
    }

    [Fact]
    public void ARecordThatDoesNotParse_AndCannotBeSetAside_IsLeftAsItIs()
    {
        File.WriteAllText(StoreFile, "{ not json");
        // A folder where the set-aside copy would go: the move fails, and a write to the file itself would not.
        Directory.CreateDirectory(StoreFile + ".unreadable");

        Assert.Same(ProgramsLook.Unreadable, NewHistory().Observe(FirstLook));
        Assert.Equal("{ not json", File.ReadAllText(StoreFile));
    }

    [Fact]
    public void Parse_FillsWhatTheFileLeftOut_AndDropsNamelessEntries()
    {
        var json = JsonSerializer.Serialize(new InstalledProgramsHistory.Stored(FirstLook,
            [new ProgramName("Contoso Notes", null!), new ProgramName("", "Contoso")],
            [new ProgramChange("Contoso Paint", null!, ChangeKind.ProgramAppeared, FirstLook, SecondLook, null!),
             new ProgramChange("", "Contoso", ChangeKind.ProgramAppeared, FirstLook, SecondLook)]));

        var stored = InstalledProgramsHistory.Parse(json);

        Assert.NotNull(stored);
        Assert.Equal(new ProgramName("Contoso Notes", ""), Assert.Single(stored!.Programs));
        var change = Assert.Single(stored.Changes);
        Assert.Equal(("", ""), (change.Publisher, change.Before));
    }

    [Fact]
    public void Parse_WithNoChangesAtAll_ReadsAsNone()
    {
        var json = JsonSerializer.Serialize(new InstalledProgramsHistory.Stored(FirstLook, [new ProgramName("Contoso Notes", "Contoso")], null!));

        Assert.Empty(InstalledProgramsHistory.Parse(json)!.Changes);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{ not json")]
    public void Parse_OfSomethingThatIsNotAHistory_IsNull(string json)
    {
        Assert.Null(InstalledProgramsHistory.Parse(json));
    }

    [Fact]
    public void Parse_OfAHistoryWithNoProgramList_IsNull()
    {
        var json = JsonSerializer.Serialize(new InstalledProgramsHistory.Stored(FirstLook, null!, []));

        Assert.Null(InstalledProgramsHistory.Parse(json));
    }

    // ── How a change's kind is stored ──────────────────────────────────────

    [Fact]
    public void TheKindOfAChange_IsWrittenByName()
    {
        // As a number, a stored kind would read as another one the day ChangeKind gained a member before the program
        // kinds, and the record keeps its changes for months.
        Installed(Program("Contoso Notes"));
        NewHistory().Observe(FirstLook);
        Installed(Program("Contoso Notes"), Program("Contoso Paint"));
        NewHistory().Observe(SecondLook);

        var text = File.ReadAllText(StoreFile);
        Assert.Contains("\"Kind\":\"ProgramAppeared\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AKindAnEarlierBuildWroteAsANumber_StillReads()
    {
        const string json = """
            {"TakenAt":"2026-10-02T09:00:00","Programs":[{"Name":"Contoso Notes","Publisher":"Contoso"}],
             "Changes":[{"Name":"Contoso Paint","Publisher":"Contoso","Kind":9,
                         "Since":"2026-10-02T09:00:00","Until":"2026-10-05T15:20:00","Before":""}]}
            """;

        var change = Assert.Single(InstalledProgramsHistory.Parse(json)!.Changes);

        Assert.Equal(ChangeKind.ProgramDisappeared, change.Kind);
    }

    [Fact]
    public void TheProgramKinds_KeepThePositionsEarlierBuildsWroteThemAt()
    {
        // Records written before the kind was stored by name hold these numbers, so moving any of the three would make
        // those records read wrong.
        Assert.Equal(8, (int)ChangeKind.ProgramAppeared);
        Assert.Equal(9, (int)ChangeKind.ProgramDisappeared);
        Assert.Equal(10, (int)ChangeKind.ProgramUpdated);
    }

    [Fact]
    public void AChangeOfAKindThisRecordNeverHolds_IsDropped()
    {
        var json = JsonSerializer.Serialize(new InstalledProgramsHistory.Stored(FirstLook,
            [new ProgramName("Contoso Notes", "Contoso")],
            [new ProgramChange("Contoso Paint", "Contoso", ChangeKind.ProgramRemoved, FirstLook, SecondLook),
             new ProgramChange("Contoso Draw", "Contoso", ChangeKind.ProgramAppeared, FirstLook, SecondLook)]));

        Assert.Equal("Contoso Draw", Assert.Single(InstalledProgramsHistory.Parse(json)!.Changes).Name);
    }

    // ── Names ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Contoso Notes", false, false, true)]
    [InlineData("", false, false, false)]
    [InlineData("Contoso Notes Helper", true, false, false)]
    [InlineData("Contoso Notes KB12", false, true, false)]
    public void AnEntryIsAProgram_OnlyWithANameAndNeitherHiddenNorAnUpdate(string name, bool hidden, bool update, bool listed)
    {
        Assert.Equal(listed, Program(name, hidden: hidden, update: update).IsListed);
    }

    [Theory]
    [InlineData("Python 3.12.1 (64-bit)", "Python # (64-bit)", "3.12.1")]
    [InlineData("7-Zip 23.01 (x64)", "7-Zip # (x64)", "23.01")]
    [InlineData("Contoso Notes v2.4", "Contoso Notes #", "v2.4")]
    [InlineData("Contoso Notes 2015-2022", "Contoso Notes #", "2015-2022")]
    [InlineData("Left 4 Dead 2", "Left # Dead #", "4 2")]
    [InlineData("Contoso Notes", "Contoso Notes", "")]
    public void AName_IsItsWordsWithTheVersionNumbersMarked_AndThoseNumbers(string name, string shape, string versions)
    {
        Assert.Equal(shape, InstalledProgramsHistory.Shape(name));
        Assert.Equal(versions, InstalledProgramsHistory.Versions(name));
    }

    // ── Which program an update replaced (#2779) ──────────────────────────

    private static readonly DateTime Since = new(2026, 11, 1, 9, 0, 0);
    private static readonly DateTime Until = new(2026, 11, 2, 9, 0, 0);

    private const string Psf = "Python Software Foundation";

    private static ProgramName Named(string name, string publisher = Psf) => new(name, publisher);

    private static (string Name, string Before)[] Updates(IEnumerable<ProgramChange> changes) =>
        [.. changes.Where(c => c.Kind == ChangeKind.ProgramUpdated)
            .Select(c => (c.Name, c.Before))
            .OrderBy(u => u.Name)];

    /// <summary>
    /// Two lines of one product updated together, as one "Update all" does: each keeps its own line, whatever order the
    /// uninstall keys list them in. Both read "Python (64-bit)" once every number is taken out, and the first one found
    /// was taken.
    /// </summary>
    [Fact]
    public void TwoLinesOfOneProductUpdatedTogether_EachReplaceTheirOwn_InEitherOrder()
    {
        (string, string)[] expected =
        [
            ("Python 3.12.7 (64-bit)", "Python 3.12.6 (64-bit)"),
            ("Python 3.13.1 (64-bit)", "Python 3.13.0 (64-bit)"),
        ];

        Assert.Equal(expected, Updates(InstalledProgramsHistory.Compare(
            [Named("Python 3.12.6 (64-bit)"), Named("Python 3.13.0 (64-bit)")],
            [Named("Python 3.12.7 (64-bit)"), Named("Python 3.13.1 (64-bit)")], Since, Until)));
        // The new names listed the other way round from the old ones: the first old name found was the other line's.
        Assert.Equal(expected, Updates(InstalledProgramsHistory.Compare(
            [Named("Python 3.12.6 (64-bit)"), Named("Python 3.13.0 (64-bit)")],
            [Named("Python 3.13.1 (64-bit)"), Named("Python 3.12.7 (64-bit)")], Since, Until)));
    }

    /// <summary>
    /// Two products whose names differ by a number are two products: "Left 4 Dead" and "Left 4 Dead 2" both read
    /// "Left Dead" once every number is taken out, and removing one and installing the other was an update.
    /// </summary>
    [Fact]
    public void TwoProductsWhoseNamesDifferByANumber_AreAnInstallAndARemoval_NotAnUpdate()
    {
        var changes = InstalledProgramsHistory.Compare(
            [Named("Left 4 Dead", "Valve")], [Named("Left 4 Dead 2", "Valve")], Since, Until);

        Assert.Equal([ChangeKind.ProgramAppeared, ChangeKind.ProgramDisappeared], changes.Select(c => c.Kind));
    }

    [Fact]
    public void AProductWithOneLine_IsStillAnUpdate_WhateverItsVersionsShare()
    {
        var change = Assert.Single(InstalledProgramsHistory.Compare(
            [Named("7-Zip 23.01 (x64)", "Igor Pavlov")], [Named("7-Zip 24.08 (x64)", "Igor Pavlov")], Since, Until));

        Assert.Equal((ChangeKind.ProgramUpdated, "7-Zip 23.01 (x64)"), (change.Kind, change.Before));
    }

    /// <summary>
    /// Two old names the new one could have replaced, sharing as much with it: a guess either way, so neither.
    /// </summary>
    [Fact]
    public void ATieBetweenTwoOldNames_IsAnInstallAndRemovals_NotAGuess()
    {
        var changes = InstalledProgramsHistory.Compare(
            [Named("Contoso Tool 1.0", "Contoso"), Named("Contoso Tool 2.0", "Contoso")],
            [Named("Contoso Tool 3.0", "Contoso")], Since, Until);

        Assert.Equal([ChangeKind.ProgramAppeared, ChangeKind.ProgramDisappeared, ChangeKind.ProgramDisappeared],
            changes.Select(c => c.Kind));
    }
}
