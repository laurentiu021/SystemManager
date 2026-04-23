// SysManager · DuplicateFileGroup — model for duplicate file results
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// A group of files that share the same content (identical SHA-256 hash).
/// Read-only by design — the UI only offers "Show in Explorer" and "Copy path".
/// </summary>
public partial class DuplicateFileGroup : ObservableObject
{
    [ObservableProperty] private string _hash = "";
    [ObservableProperty] private long _fileSize;
    [ObservableProperty] private int _count;

    public ObservableCollection<DuplicateFileEntry> Files { get; } = new();

    /// <summary>Wasted space = (count - 1) * fileSize.</summary>
    public long WastedBytes => (Count - 1) * FileSize;
}

/// <summary>A single file within a duplicate group.</summary>
public partial class DuplicateFileEntry : ObservableObject
{
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private DateTime _lastModified;
    [ObservableProperty] private bool _isSelected;
}
