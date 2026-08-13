// SysManager · FixedShapeDateFormatTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Pins the dates that must look identical on every machine.
/// <para>Windows sets <c>CultureInfo.CurrentCulture</c> from the user's regional settings, so a custom
/// pattern with no <c>IFormatProvider</c> renders differently per install — and under a non-Gregorian
/// default calendar it renders a different DATE. Measured for 2026-08-04 13:45:30:</para>
/// <list type="bullet">
/// <item>th-TH (Buddhist calendar): <c>yyyy-MM-dd</c> → <c>2569-08-04</c></item>
/// <item>ar-SA (Umm al-Qura): <c>yyyy-MM-dd</c> → <c>1448-02-21</c> — month too</item>
/// <item>fi-FI: <c>HH:mm</c> → <c>13.45</c>, because ':' is the culture's time separator</item>
/// </list>
/// <para>These run the real display members under those cultures. Every case here FAILED before the
/// fix on at least one locale, which is what makes them a regression test rather than a restatement.
/// Grouped in one file because it is one defect class, not one type's behaviour.</para>
/// </summary>
public sealed class FixedShapeDateFormatTests
{
    private static readonly DateTime Moment = new(2026, 8, 4, 13, 45, 30, DateTimeKind.Utc);

    /// <summary>ar-SA and th-TH shift the calendar; fi-FI shifts the time separator.</summary>
    public static TheoryData<string> HostileCultures() => new() { "ar-SA", "th-TH", "fi-FI", "de-DE", "en-US", "" };

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void BootRecord_WhenDisplay_IsTheSameOnEveryLocale(string culture)
    {
        var record = new BootRecord(Moment, 30_000, 12_000, 18_000);

        WithCulture(culture, () => Assert.Equal("2026-08-04 13:45", record.WhenDisplay));
    }

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void PrivacyAccessEntry_LastUsedDisplay_StaysISO(string culture)
    {
        // Shown in the Privacy Monitor grid under a "Last used" column and sorted as text, so a
        // Buddhist year would both misreport the date and break the ordering.
        var entry = new PrivacyAccessEntry("Camera", "Camera app", Moment, false);

        WithCulture(culture, () => Assert.Equal("2026-08-04 13:45", entry.LastUsedDisplay));
    }

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void LargeFileEntry_LastModifiedDisplay_NamesTheMonthInEnglish(string culture)
    {
        // The UI ships no translations, so a Finnish "elok." or Arabic "صفر" next to English column
        // headers is not localization — it is one field speaking a different language than its label.
        var entry = new LargeFileEntry
        {
            Path = @"C:\test\file.zip",
            Name = "file.zip",
            SizeBytes = 1000,
            LastModified = Moment
        };

        WithCulture(culture, () => Assert.Equal("04 Aug 2026", entry.LastModifiedDisplay));
    }

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void BandwidthAxisTicks_AreStableOnEveryLocale(string culture)
    {
        var ticks = Moment.Ticks;

        WithCulture(culture, () =>
        {
            Assert.Equal("13:45:30", BandwidthMonitorViewModel.FormatAxisTick(ticks, TimeSpan.Zero));
            Assert.Equal("13:45", BandwidthMonitorViewModel.FormatAxisTick(ticks, TimeSpan.FromHours(1)));
            Assert.Equal("08-04 13:45", BandwidthMonitorViewModel.FormatAxisTick(ticks, TimeSpan.FromDays(7)));
        });
    }

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void WindowsUpdatePolicy_Summary_StatesTheRealPauseDate(string culture)
    {
        // This one is read as a promise ("paused until X"), so a wrong year is worse than an ugly one.
        var policy = new WindowsUpdatePolicy(
            DeferFeatureUpdates: false, FeatureDeferDays: 0, PauseActive: true, PauseUntil: Moment);

        WithCulture(culture, () => Assert.Equal("Updates paused until 2026-08-04.", policy.Summary));
    }

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void RestorePoint_CreatedDisplay_IsTheSameOnEveryLocale(string culture)
    {
        var point = new RestorePoint(42, "Before driver update", Moment, "APPLICATION_INSTALL", "BEGIN_SYSTEM_CHANGE");

        WithCulture(culture, () => Assert.Equal("2026-08-04 13:45", point.CreatedDisplay));
    }

    [Fact]
    public void TheHostileCultures_ReallyDoDisagree_AboutTheseDates()
    {
        // Guards the guard. If a future runtime resolved ar-SA to the Gregorian calendar, every test
        // above would pass while proving nothing — so assert the danger is real on this machine, and
        // fail loudly if the premise stops holding rather than reporting false safety.
        var thai = new CultureInfo("th-TH");
        var saudi = new CultureInfo("ar-SA");
        var finnish = new CultureInfo("fi-FI");

        Assert.NotEqual("2026", Moment.ToString("yyyy", thai));
        Assert.NotEqual("2026", Moment.ToString("yyyy", saudi));
        Assert.NotEqual("13:45", Moment.ToString("HH:mm", finnish));

        Assert.Equal("2026-08-04 13:45", Moment.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Runs <paramref name="assert"/> with the thread's culture switched, restoring it in a finally so
    /// one test cannot leak a locale into the rest of the run. Deterministic: no ambient state is read.
    /// </summary>
    private static void WithCulture(string culture, Action assert)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture.Length == 0
                ? CultureInfo.InvariantCulture
                : new CultureInfo(culture);
            assert();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
