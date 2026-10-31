// SysManager · UnappliedChangesTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// The words four tabs use before Refresh reads their list again over changes not made yet (#2734). Pinned here, so a
/// change to them is made on purpose: each tab's own tests compare with these methods, and would follow any wording.
/// </summary>
public class UnappliedChangesTests
{
    [Theory]
    [InlineData("Apply", "Refresh, and lose your pending changes?\n\nRefresh reads the list again from Windows, without "
        + "them. Choose No to keep them, then press Apply to make them.")]
    [InlineData("Save", "Refresh, and lose your pending changes?\n\nRefresh reads the list again from Windows, without "
        + "them. Choose No to keep them, then press Save to make them.")]
    public void TheQuestion_SaysWhatRefreshDoes_AndWhatNoKeeps(string button, string expected)
        => Assert.Equal(expected, UnappliedChanges.Question(button));

    [Theory]
    [InlineData("Apply", "Refresh cancelled. Your pending changes are kept — press Apply to make them.")]
    [InlineData("Save", "Refresh cancelled. Your pending changes are kept — press Save to make them.")]
    public void WhatTheTabSays_WhenTheChangesAreKept(string button, string expected)
        => Assert.Equal(expected, UnappliedChanges.Kept(button));

    [Fact]
    public void TheTitle_IsWordedLikeTheOtherConfirmations()
        => Assert.Equal("Confirm Refresh", UnappliedChanges.Title);
}
