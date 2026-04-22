// SysManager · AppPackageTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.ComponentModel;
using SysManager.Models;

namespace SysManager.Tests;

public class AppPackageTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var p = new AppPackage();
        Assert.True(p.IsSelected);            // selected by default for bulk ops
        Assert.Equal("", p.Name);
        Assert.Equal("", p.Id);
        Assert.Equal("", p.CurrentVersion);
        Assert.Equal("", p.AvailableVersion);
        Assert.Equal("winget", p.Source);
        Assert.Equal("Pending", p.Status);
    }

    [Fact]
    public void IsSelected_RaisesPropertyChanged()
    {
        var p = new AppPackage();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)p).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        p.IsSelected = false;
        Assert.Contains(nameof(AppPackage.IsSelected), raised);
    }

    [Fact]
    public void Status_Transitions_ArePossible()
    {
        var p = new AppPackage();
        foreach (var s in new[] { "Pending", "Upgrading...", "Done", "Failed (exit 1)" })
        {
            p.Status = s;
            Assert.Equal(s, p.Status);
        }
    }

    [Fact]
    public void ScenarioLikeRealData_IsStored()
    {
        var p = new AppPackage
        {
            Name = "Visual Studio Code",
            Id = "Microsoft.VisualStudioCode",
            CurrentVersion = "1.94.0",
            AvailableVersion = "1.95.0",
            Source = "winget"
        };
        Assert.Equal("Microsoft.VisualStudioCode", p.Id);
        Assert.Equal("1.94.0", p.CurrentVersion);
        Assert.Equal("1.95.0", p.AvailableVersion);
    }

    [Fact]
    public void NamePropertyChange_RaisesEvent()
    {
        var p = new AppPackage();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)p).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        p.Name = "Git";
        p.Id = "Git.Git";
        p.CurrentVersion = "2.47.0";
        p.AvailableVersion = "2.48.0";
        p.Source = "msstore"; // change from default "winget" to trigger event
        p.Status = "Upgrading...";
        Assert.Contains(nameof(AppPackage.Name), raised);
        Assert.Contains(nameof(AppPackage.Id), raised);
        Assert.Contains(nameof(AppPackage.CurrentVersion), raised);
        Assert.Contains(nameof(AppPackage.AvailableVersion), raised);
        Assert.Contains(nameof(AppPackage.Source), raised);
        Assert.Contains(nameof(AppPackage.Status), raised);
    }
}
