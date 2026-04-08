// Copyright (c) Joshua Kidder. All rights reserved.
// Licensed under the MIT License.

using System;

namespace Microsoft.PowerShell.RunspaceComposer;

/// <summary>
/// Configuration for <see cref="RunspaceComposer"/> behavior.
/// </summary>
public sealed record RunspaceComposerOptions
{
    /// <summary>
    /// Maximum time allowed for a single sync cycle on a runspace.
    /// If exceeded, the sync is abandoned and the runspace is marked <see cref="RunspaceHealthState.Degraded"/>.
    /// </summary>
    public TimeSpan SyncTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum number of consecutive sync failures before a runspace is marked <see cref="RunspaceHealthState.Faulted"/>.
    /// </summary>
    public int MaxSyncRetries { get; init; } = 3;

    /// <summary>
    /// Whether to track per-runspace usage statistics (invocation count, last used time).
    /// </summary>
    public bool TrackUsageStatistics { get; init; } = true;
}
