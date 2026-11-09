// SysManager · GridTextTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using SysManager.Helpers;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// <see cref="GridText"/> gives a list's text columns the cell style it holds, and leaves every other column alone
/// (#3214).
/// </summary>
/// <remarks>
/// <c>ListCellTextTests</c> builds the views and reads a cut cell; these pin the rules with no Application and a style
/// made here, so nothing but the property decides which column gets what.
/// </remarks>
public class GridTextTests
{
    private static Style CellStyle() => new(typeof(TextBlock), DataGridTextColumn.DefaultElementStyle);

    [StaFact]
    public void ACellStyle_ReachesTheTextColumnsAListHas_AndTheOnesItIsGivenAfter()
    {
        var grid = new DataGrid();
        var had = new DataGridTextColumn();
        grid.Columns.Add(had);
        var style = CellStyle();

        GridText.SetCellStyle(grid, style);
        var added = new DataGridTextColumn();
        grid.Columns.Add(added);

        Assert.Same(style, GridText.GetCellStyle(grid));
        Assert.Same(style, had.ElementStyle);
        Assert.Same(style, added.ElementStyle);
    }

    [StaFact]
    public void AColumnWithAStyleOfItsOwn_OrNotOfText_IsLeftAsItIs()
    {
        var grid = new DataGrid();
        var own = new Style(typeof(TextBlock));
        var styled = new DataGridTextColumn { ElementStyle = own };
        var check = new DataGridCheckBoxColumn();
        grid.Columns.Add(styled);
        grid.Columns.Add(check);
        var defaultCheckStyle = check.ElementStyle;

        GridText.SetCellStyle(grid, CellStyle());
        var styledLater = new DataGridTextColumn { ElementStyle = own };
        grid.Columns.Add(styledLater);

        Assert.Same(own, styled.ElementStyle);
        Assert.Same(own, styledLater.ElementStyle);
        Assert.Same(defaultCheckStyle, check.ElementStyle);
    }

    [StaFact]
    public void AnotherCellStyle_TakesThePlaceOfTheOneTheListGave_AndNoneGivesWpfsBack()
    {
        var grid = new DataGrid();
        var column = new DataGridTextColumn();
        var own = new Style(typeof(TextBlock));
        var styled = new DataGridTextColumn { ElementStyle = own };
        grid.Columns.Add(column);
        grid.Columns.Add(styled);
        var first = CellStyle();
        var second = CellStyle();

        GridText.SetCellStyle(grid, first);
        GridText.SetCellStyle(grid, second);
        Assert.Same(second, column.ElementStyle);
        Assert.Same(own, styled.ElementStyle);

        GridText.SetCellStyle(grid, null);
        var addedAfter = new DataGridTextColumn();
        grid.Columns.Add(addedAfter);

        Assert.Same(DataGridTextColumn.DefaultElementStyle, column.ElementStyle);
        Assert.Same(own, styled.ElementStyle);
        Assert.Same(DataGridTextColumn.DefaultElementStyle, addedAfter.ElementStyle);
    }
}
