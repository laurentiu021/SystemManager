// SysManager · FocusWhenShownTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// When <see cref="FocusWhenShown"/> moves focus to a control that appears, and that Display Profiles' Keep uses it
/// (#2883).
/// </summary>
/// <remarks>
/// Moving focus needs a window on screen, so these pin the choice that decides WHETHER it moves, as
/// <c>RebuildFocusTests</c> do for where it goes. A row of controls is built without a window: a panel holding Keep and
/// a box, inside another panel standing in for the page.
/// </remarks>
public class FocusWhenShownTests
{
    private sealed record Page(StackPanel Window, StackPanel Bar, Button Keep, TextBox Elsewhere);

    private static Page Built()
    {
        var keep = new Button { Content = "Keep" };
        var bar = new StackPanel { Children = { keep } };
        var elsewhere = new TextBox();
        var window = new StackPanel { Children = { bar, elsewhere } };
        return new Page(window, bar, keep, elsewhere);
    }

    [StaFact]
    public void FocusNowhere_IsTaken()
    {
        var page = Built();
        Assert.True(FocusWhenShown.FocusIsUnchosen(null, page.Keep, page.Window));
    }

    [StaFact]
    public void FocusOnTheWindow_IsTaken()
    {
        var page = Built();
        Assert.True(FocusWhenShown.FocusIsUnchosen(page.Window, page.Keep, page.Window));
    }

    [StaFact]
    public void FocusOnAnAncestor_WhereWpfLeavesItFromADisabledControl_IsTaken()
    {
        var page = Built();
        Assert.True(FocusWhenShown.FocusIsUnchosen(page.Bar, page.Keep, page.Window));
    }

    [StaFact]
    public void FocusTheUserMovedToAnotherControl_StaysThere()
    {
        var page = Built();
        Assert.False(FocusWhenShown.FocusIsUnchosen(page.Elsewhere, page.Keep, page.Window));
    }

    [StaFact]
    public void FocusAlreadyOnTheControl_IsNotMovedAgain()
    {
        var page = Built();
        Assert.False(FocusWhenShown.FocusIsUnchosen(page.Keep, page.Keep, page.Window));
    }

    [Fact]
    public void DisplayProfilesKeep_TakesFocusWhenTheBarAppears()
    {
        var helpers = XNamespace.Get("clr-namespace:SysManager.Helpers");
        var view = XDocument.Load(TestPaths.AppFile("Views", "DisplayProfileView.xaml"));
        var keep = Assert.Single(view.Descendants(), e => e.Name.LocalName == "Button"
            && (string?)e.Attribute("Command") == "{Binding KeepSettingsCommand}");

        Assert.Equal("True", (string?)keep.Attribute(helpers + "FocusWhenShown.Take"));
    }
}
