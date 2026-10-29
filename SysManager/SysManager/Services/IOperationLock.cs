// SysManager · IOperationLock — abstraction for OperationLockService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Abstraction for <see cref="OperationLockService"/>, so a view model can take it through its
/// constructor instead of reaching for <see cref="OperationLockService.Instance"/> directly (#2860).
/// <see cref="OperationLockService"/>'s own constructor stays private — this interface does not open
/// a way to construct a second real lock table, only a way for a test to substitute one.
/// </summary>
public interface IOperationLock
{
    /// <inheritdoc cref="OperationLockService.TryAcquire"/>
    OperationLockService.OperationHandle? TryAcquire(OperationCategory category, string operationName);

    /// <inheritdoc cref="OperationLockService.IsLocked"/>
    bool IsLocked(OperationCategory category);

    /// <inheritdoc cref="OperationLockService.GetActiveOperationName"/>
    string? GetActiveOperationName(OperationCategory category);

    /// <inheritdoc cref="OperationLockService.ActiveOperations"/>
    IReadOnlyList<(OperationCategory Category, OperationInfo Info)> ActiveOperations { get; }

    /// <inheritdoc cref="OperationLockService.HasActiveOperations"/>
    bool HasActiveOperations { get; }
}
