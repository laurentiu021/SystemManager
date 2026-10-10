// SysManager · RebuildFocusTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Where <see cref="RebuildFocus"/> puts keyboard focus after a list's rows are rebuilt (#2609).
/// </summary>
/// <remarks>
/// Moving focus needs a window on screen, so these pin the choices that decide WHERE it goes: which row, and which control
/// in it. The list tests build a real ItemsControl with a row template and lay it out without a window, which is enough
/// for WPF to generate rows. The UI test <c>Services_F5_PutsKeyboardFocusBackOnTheSameRow</c> drives the move itself.
/// </remarks>
public class RebuildFocusTests
{
    private const string RowTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <StackPanel Orientation="Horizontal">
                <TextBlock Text="{Binding Name}"/>
                <Button Content="Stop" AutomationProperties.Name="{Binding StopName}"/>
                <CheckBox AutomationProperties.Name="{Binding Name}"/>
            </StackPanel>
        </DataTemplate>
        """;

    /// <summary>A row the way a refresh builds it: a new object every time, equal only to itself.</summary>
    public sealed class Row(string name)
    {
        public string Name { get; } = name;
        public string StopName => $"Stop {Name}";
    }

    private static RebuildFocus.Place At(int index, object? item = null, string? name = null, int ordinal = 0,
        Type? kind = null) => new(item, index, name, ordinal, kind ?? typeof(Button));

    private static T Named<T>(T element, string name) where T : UIElement
    {
        AutomationProperties.SetName(element, name);
        return element;
    }

    // ---------- Which row ----------

    [Fact]
    public void RowFor_TheSameItemStillListed_IsThatItemsRow()
    {
        var kept = new Row("Spooler");
        object[] items = [new Row("Audio"), new Row("BITS"), kept];

        Assert.Equal(2, RebuildFocus.RowFor(At(0, item: kept), items, namedRow: 1));
    }

    [Fact]
    public void RowFor_AnItemThatWasReplaced_IsTheRowHoldingTheSameName()
    {
        object[] items = [new Row("Audio"), new Row("BITS"), new Row("Spooler")];

        Assert.Equal(2, RebuildFocus.RowFor(At(0, item: new Row("Spooler")), items, namedRow: 2));
    }

    [Fact]
    public void RowFor_NoItemAndNoName_IsTheRowNowAtTheSamePosition()
    {
        object[] items = [new Row("Audio"), new Row("BITS"), new Row("Spooler")];

        Assert.Equal(1, RebuildFocus.RowFor(At(1, item: new Row("Gone")), items, namedRow: -1));
    }

    [Fact]
    public void RowFor_APositionPastTheLastRow_IsTheLastRow()
    {
        // The last row was the one removed, so focus goes to the row that is now last.
        object[] items = [new Row("Audio"), new Row("BITS")];

        Assert.Equal(1, RebuildFocus.RowFor(At(2), items, namedRow: -1));
    }

    [Fact]
    public void RowFor_ANamedRowPastTheEnd_IsIgnored()
    {
        object[] items = [new Row("Audio"), new Row("BITS")];

        Assert.Equal(0, RebuildFocus.RowFor(At(0), items, namedRow: 5));
    }

    [Fact]
    public void RowFor_AnEmptyList_IsNoRow()
    {
        Assert.Equal(-1, RebuildFocus.RowFor(At(0, item: new Row("Audio")), Array.Empty<object>(), namedRow: -1));
    }

    // ---------- Which control in the row ----------

    [StaFact]
    public void ControlFor_TheSameName_WinsOverThePosition()
    {
        var start = Named(new Button(), "Start Spooler");
        var stop = Named(new Button(), "Stop Spooler");

        Assert.Same(stop, RebuildFocus.ControlFor(At(0, name: "Stop Spooler", ordinal: 0), [start, stop]));
    }

    [StaFact]
    public void ControlFor_ANameTwoControlsShare_FallsBackToThePosition()
    {
        var first = Named(new Button(), "Remove");
        var second = Named(new Button(), "Remove");

        Assert.Same(second, RebuildFocus.ControlFor(At(0, name: "Remove", ordinal: 1), [first, second]));
    }

    [StaFact]
    public void ControlFor_ThePositionHoldingAnotherKind_TakesTheFirstOfTheSameKind()
    {
        // A row with one control fewer than the row focus was on: position 1 is now the checkbox.
        var mark = new Button();
        var tick = new CheckBox();

        Assert.Same(mark, RebuildFocus.ControlFor(At(0, ordinal: 1, kind: typeof(Button)), [mark, tick]));
        Assert.Same(tick, RebuildFocus.ControlFor(At(0, ordinal: 0, kind: typeof(CheckBox)), [mark, tick]));
    }

    [StaFact]
    public void ControlFor_NoControlOfTheSameKind_TakesTheFirst()
    {
        var tick = new CheckBox();

        Assert.Same(tick, RebuildFocus.ControlFor(At(0, ordinal: 0, kind: typeof(Button)), [tick]));
    }

    [StaFact]
    public void ControlFor_ARowWithNoControls_IsNull()
    {
        Assert.Null(RebuildFocus.ControlFor(At(0), []));
    }

    [StaFact]
    public void FocusablesIn_SkipsHiddenDisabledAndUnfocusableElements_InTreeOrder()
    {
        var first = new Button();
        var hidden = new Button();
        var disabled = new Button { IsEnabled = false };
        var nested = new CheckBox();
        var root = new StackPanel
        {
            Children =
            {
                first,
                new Border { Visibility = Visibility.Collapsed, Child = hidden },
                disabled,
                new TextBlock(),
                new StackPanel { Children = { nested } },
            },
        };

        Assert.Equal<UIElement>([first, nested], RebuildFocus.FocusablesIn(root));
    }

    [StaFact]
    public void FocusablesIn_CountsTheRowItself_WhenItCanTakeFocus()
    {
        var row = new Button();

        Assert.Equal<UIElement>([row], RebuildFocus.FocusablesIn(row));
    }

    // ---------- A real list, rebuilt ----------

    /// <summary>New rows, the way a refresh builds them.</summary>
    private static IEnumerable<Row> Rows(params string[] names) => names.Select(n => new Row(n));

    private static (ItemsControl List, BulkObservableCollection<Row> Rows) ListOf(params string[] names)
    {
        var rows = new BulkObservableCollection<Row>();
        rows.ReplaceWith(Rows(names));
        // Initialised explicitly: an element built in code with no parent never looks up its theme style, so it would
        // have no template, and an ItemsControl without one generates no rows.
        var list = new ItemsControl();
        list.BeginInit();
        list.ItemTemplate = (DataTemplate)XamlReader.Parse(RowTemplate);
        list.ItemsSource = rows;
        list.EndInit();
        Lay(list);
        return (list, rows);
    }

    private static void Lay(ItemsControl list)
    {
        list.InvalidateMeasure();
        list.Measure(new Size(400, 600));
        list.Arrange(new Rect(0, 0, 400, 600));
        list.UpdateLayout();
    }

    private static Button StopButtonIn(ItemsControl list, int row) =>
        RebuildFocus.FocusablesIn(list.ItemContainerGenerator.ContainerFromIndex(row)).OfType<Button>().Single();

    /// <summary>The row <paramref name="element"/> is in, so a failure says which row focus would go to.</summary>
    private static int RowOf(ItemsControl list, UIElement? element) =>
        element is null ? -1 : list.ItemContainerGenerator.IndexFromContainer(list.ContainerFromElement(element));

    [StaFact]
    public void ARebuildWithNewObjects_SendsFocusBackToTheSameItemsControl_WhereverItsRowNowIs()
    {
        var (list, rows) = ListOf("Audio", "BITS", "Spooler");
        var place = RebuildFocus.PlaceOf(list, StopButtonIn(list, 1));
        Assert.NotNull(place);
        Assert.Equal(1, place!.Index);
        Assert.Equal("Stop BITS", place.Name);

        // A refresh that builds every row anew, and lists a new service above the one focus was on.
        rows.ReplaceWith(Rows("Audio", "Appinfo", "BITS", "Spooler"));
        Lay(list);

        var target = RebuildFocus.TargetFor(list, place);
        Assert.Equal(2, RowOf(list, target));
        Assert.Same(StopButtonIn(list, 2), target);
        Assert.Equal("Stop BITS", AutomationProperties.GetName(target!));
    }

    [StaFact]
    public void ARebuildWithoutTheFocusedRow_SendsFocusToTheRowNowInItsPlace()
    {
        // Putting a change back removes its row, so focus goes on to the next one.
        var (list, rows) = ListOf("Audio", "BITS", "Spooler");
        var place = RebuildFocus.PlaceOf(list, StopButtonIn(list, 1))!;

        rows.ReplaceWith(Rows("Audio", "Spooler"));
        Lay(list);

        var target = RebuildFocus.TargetFor(list, place);
        Assert.Equal(1, RowOf(list, target));
        Assert.Same(StopButtonIn(list, 1), target);
    }

    [StaFact]
    public void ARowRemovedOnItsOwn_SendsFocusToTheRowNowInItsPlace()
    {
        // Ping's remove button and an Audio Mixer session ending take one row out, with no rebuild around it (#2650).
        var (list, rows) = ListOf("Audio", "BITS", "Spooler");
        var place = RebuildFocus.PlaceOf(list, StopButtonIn(list, 1))!;

        rows.RemoveAt(1);
        Lay(list);

        var target = RebuildFocus.TargetFor(list, place);
        Assert.Equal(1, RowOf(list, target));
        Assert.Same(StopButtonIn(list, 1), target);
        Assert.Equal("Stop Spooler", AutomationProperties.GetName(target!));
    }

    [StaFact]
    public void TheLastRowRemovedOnItsOwn_SendsFocusToTheRowNowLast()
    {
        var (list, rows) = ListOf("Audio", "BITS");
        var place = RebuildFocus.PlaceOf(list, StopButtonIn(list, 1))!;

        rows.RemoveAt(1);
        Lay(list);

        var target = RebuildFocus.TargetFor(list, place);
        Assert.Equal(0, RowOf(list, target));
        Assert.Equal("Stop Audio", AutomationProperties.GetName(target!));
    }

    [StaFact]
    public void ANameTwoRowsShare_DoesNotPickARow_SoThePositionDoes()
    {
        // Two rows named alike, the way two programs can share a display name. The name says nothing about which one
        // focus was on, so the position decides.
        var (list, rows) = ListOf("Audio", "BITS", "Audio");
        var place = RebuildFocus.PlaceOf(list, StopButtonIn(list, 0))!;

        rows.ReplaceWith(Rows("Audio", "BITS", "Audio"));
        Lay(list);

        var target = RebuildFocus.TargetFor(list, place);
        Assert.Equal(0, RowOf(list, target));
        Assert.Same(StopButtonIn(list, 0), target);
    }

    [StaFact]
    public void ARebuildThatKeepsTheSameObjects_FollowsTheItem()
    {
        var (list, rows) = ListOf("Audio", "BITS", "Spooler");
        var spooler = rows[2];
        var place = RebuildFocus.PlaceOf(list, StopButtonIn(list, 2))!;

        rows.ReplaceWith([spooler, rows[0], rows[1]]);
        Lay(list);

        var target = RebuildFocus.TargetFor(list, place);
        Assert.Equal(0, RowOf(list, target));
        Assert.Same(StopButtonIn(list, 0), target);
    }

    [StaFact]
    public void ARebuildThatEmptiesTheList_HasNoControlToFocus()
    {
        var (list, rows) = ListOf("Audio");
        var place = RebuildFocus.PlaceOf(list, StopButtonIn(list, 0))!;

        rows.ReplaceWith([]);
        Lay(list);

        Assert.Null(RebuildFocus.TargetFor(list, place));
    }

    [StaFact]
    public void TheSecondControlInARow_IsTheOneFocusGoesBackTo()
    {
        var (list, rows) = ListOf("Audio", "BITS");
        var tick = RebuildFocus.FocusablesIn(list.ItemContainerGenerator.ContainerFromIndex(1)).OfType<CheckBox>().Single();
        var place = RebuildFocus.PlaceOf(list, tick)!;
        Assert.Equal(1, place.Ordinal);

        rows.ReplaceWith(Rows("Audio", "BITS"));
        Lay(list);

        var target = RebuildFocus.TargetFor(list, place);
        Assert.IsType<CheckBox>(target);
        Assert.Equal("BITS", AutomationProperties.GetName(target!));
    }

    [StaFact]
    public void PlaceOf_AnElementThatIsNotInARow_IsNull()
    {
        var (list, _) = ListOf("Audio");

        Assert.Null(RebuildFocus.PlaceOf(list, list));
        Assert.Null(RebuildFocus.PlaceOf(list, new Button()));
    }
}
