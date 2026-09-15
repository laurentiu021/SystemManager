// SysManager · RowContextMenuTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

/// <summary>
/// The per-row context menus that give the DataGrid-heavy tabs a keyboard path to their row actions
/// (#1551).
/// </summary>
/// <remarks>
/// The row buttons were labelled correctly but reachable only by arrowing to a row and then Tabbing
/// through every preceding cell, so acting on the 200th process meant 200 rows of Tabbing. A
/// <c>ContextMenu</c> on the row gives Shift+F10 and the Menu key for free.
/// <para><b>What these tests can and cannot prove.</b> They instantiate the real view on the suite's STA
/// thread with the real application resources, so a malformed binding or a missing style fails here as a
/// <c>XamlParseException</c>. They then read the menu's binding EXPRESSIONS out of the parsed objects and
/// check each path against the view model and row types by reflection — which is what pins the defect this
/// repo keeps producing: a control bound to a member that does not exist, or was renamed, and therefore
/// does nothing. What they do NOT prove is that the popup renders and the click lands; that needs the app
/// on screen, and it is a secondary-workstation check.</para>
/// <para>Reflection against the real types rather than a hardcoded list of names on purpose: rename
/// <c>KillProcessCommand</c> and this fails, which is the whole point of asserting it here rather than
/// grepping the XAML for a string that would still match itself.</para>
/// </remarks>
public partial class RowContextMenuTests
{
    /// <summary>
    /// Builds the view on the STA thread and hands the row menu to <paramref name="inspect"/> there.
    /// </summary>
    /// <remarks>
    /// Everything touching WPF stays inside the lambda. A <c>Style</c>, a <c>MenuItem</c> and a
    /// <c>BindingExpression</c> are all <c>DispatcherObject</c>s owned by the thread that made them, so
    /// returning them to xUnit's MTA thread and reading a property there throws
    /// <c>InvalidOperationException: The calling thread cannot access this object</c> — which is what the
    /// first version of this test did, and it fails in the walk rather than in an assertion, so it reads
    /// like the view being broken. <c>StaHelper.Run</c> rethrows, so assertion failures inside still
    /// surface as themselves.
    /// </remarks>
    private static void WithRowMenu<TView>(Action<List<MenuItem>, Style> inspect)
        where TView : UserControl, new()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var view = new TView();

            var grid = Descendants(view).OfType<DataGrid>().FirstOrDefault();
            Assert.NotNull(grid);

            var style = grid!.RowStyle;
            Assert.NotNull(style);

            var setter = style!.Setters.OfType<Setter>()
                .FirstOrDefault(s => s.Property == FrameworkElement.ContextMenuProperty);
            Assert.NotNull(setter);

            var menu = Assert.IsType<ContextMenu>(setter!.Value);
            inspect(menu.Items.OfType<MenuItem>().ToList(), style);
        });
    }

    /// <summary>Logical-tree walk. No layout has run, so the visual tree is not populated yet.</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var deeper in Descendants(child))
                yield return deeper;
        }
    }

    private static Binding BindingOn(MenuItem item, DependencyProperty property)
    {
        var binding = BindingOperations.GetBinding(item, property);
        Assert.NotNull(binding);
        Assert.Equal(typeof(ContextMenu), binding!.RelativeSource?.AncestorType);
        return binding;
    }

    /// <summary>The element type behind a view model's row collection, e.g. ProcessEntry.</summary>
    private static Type RowType(Type vmType, string collectionProperty)
    {
        var prop = vmType.GetProperty(collectionProperty, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(prop);
        var element = prop!.PropertyType.GetInterfaces()
            .Concat([prop.PropertyType])
            .Where(i => i.IsGenericType && typeof(IEnumerable).IsAssignableFrom(i))
            .Select(i => i.GetGenericArguments().FirstOrDefault())
            .FirstOrDefault(t => t is not null);
        Assert.NotNull(element);
        return element!;
    }

    // Captures the WHOLE identifier, not `(\w+Command)`. That form matches a PREFIX: against
    // `PlacementTarget.Tag.KillProcessCommandX` it happily returns "KillProcessCommand", which exists, so
    // renaming the command left this guard green. Found by mutation, which is the only reason it is right
    // now — a prefix-matching regex reports a real name for a path that binds to nothing.
    [GeneratedRegex(@"PlacementTarget\.Tag\.(\w+)")]
    private static partial Regex CommandPath();

    /// <summary>
    /// Every menu item is wired to a command the view model really has, with the row as its parameter.
    /// </summary>
    private static void AssertMenuIsLive(Type vmType, Type rowType, List<MenuItem> items, int minimumItems)
    {
        Assert.True(items.Count >= minimumItems,
            $"expected at least {minimumItems} menu items, parsed {items.Count} — the RowStyle's ContextMenu "
            + "has changed shape and this test would otherwise pass while inspecting almost nothing.");

        foreach (var item in items)
        {
            var command = BindingOn(item, MenuItem.CommandProperty);
            var match = CommandPath().Match(command.Path.Path);
            Assert.True(match.Success,
                $"a menu item's Command binds '{command.Path.Path}', which does not reach the view model "
                + "through PlacementTarget.Tag — so the item would be permanently disabled.");

            var name = match.Groups[1].Value;
            Assert.EndsWith("Command", name, StringComparison.Ordinal);
            var onVm = vmType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(onVm);
            Assert.True(typeof(ICommand).IsAssignableFrom(onVm!.PropertyType),
                $"{vmType.Name}.{name} is not an ICommand, so the menu item cannot invoke it.");

            // The row itself must be what the command acts on. Bound to anything else — or to nothing —
            // the item would act on whichever row happened to be selected, which is the bug this menu's
            // row anchoring exists to avoid.
            var parameter = BindingOn(item, MenuItem.CommandParameterProperty);
            Assert.Equal("PlacementTarget.DataContext", parameter.Path.Path);

            // Header and IsEnabled read the row, so their paths have to exist on the row type.
            foreach (var (property, label) in ((DependencyProperty, string)[])
                     [(HeaderedItemsControl.HeaderProperty, "Header"), (UIElement.IsEnabledProperty, "IsEnabled")])
            {
                var bound = BindingOperations.GetBinding(item, property);
                if (bound is null) continue;

                const string prefix = "PlacementTarget.DataContext.";
                Assert.StartsWith(prefix, bound.Path.Path, StringComparison.Ordinal);
                var member = bound.Path.Path[prefix.Length..];
                Assert.NotNull(rowType.GetProperty(member, BindingFlags.Public | BindingFlags.Instance));
                Assert.False(string.IsNullOrEmpty(label));
            }
        }
    }

    /// <summary>
    /// The Tag hop the menu depends on is actually set, or every Command binding resolves to nothing.
    /// </summary>
    private static void AssertTagCarriesTheViewModel(Style rowStyle)
    {
        var tag = rowStyle.Setters.OfType<Setter>()
            .FirstOrDefault(s => s.Property == FrameworkElement.TagProperty);
        Assert.NotNull(tag);

        var binding = Assert.IsType<Binding>(tag!.Value);
        Assert.Equal(typeof(DataGrid), binding.RelativeSource?.AncestorType);
        Assert.Equal("DataContext", binding.Path.Path);
    }

    [Fact]
    public void ProcessManager_RowMenu_IsWiredToTheViewModel()
        => WithRowMenu<Views.ProcessManagerView>((items, style) =>
        {
            AssertTagCarriesTheViewModel(style);
            AssertMenuIsLive(typeof(ProcessManagerViewModel),
                             RowType(typeof(ProcessManagerViewModel), "FilteredProcesses"),
                             items, minimumItems: 2);
        });

    [Fact]
    public void Services_RowMenu_IsWiredToTheViewModel()
        => WithRowMenu<Views.ServicesView>((items, style) =>
        {
            AssertTagCarriesTheViewModel(style);
            AssertMenuIsLive(typeof(ServicesViewModel),
                             RowType(typeof(ServicesViewModel), "Services"),
                             items, minimumItems: 5);
        });

    // The companion guard — that every menu command is also on a row button, so the menu cannot become a
    // way around a confirmation — is a source-shape check and lives in ArchitectureTests, next to the
    // other ones and where FindAppProjectDir already exists:
    // ArchitectureTests.EveryRowMenuCommand_IsAlsoOnARowButton.
}
