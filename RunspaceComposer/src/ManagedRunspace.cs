// Copyright (c) Joshua Kidder. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading;

namespace Microsoft.PowerShell.RunspaceComposer;

/// <summary>
/// Health classification for a managed runspace.
/// </summary>
public enum RunspaceHealthState
{
    /// <summary>Runspace is operating normally and syncing successfully.</summary>
    Healthy,

    /// <summary>Runspace has experienced sync failures but may recover.</summary>
    Degraded,

    /// <summary>Runspace has exceeded <see cref="RunspaceComposerOptions.MaxSyncRetries"/>
    /// consecutive failures and is excluded from delta compaction calculations.</summary>
    Faulted,
}

/// <summary>
/// Wraps an individual <see cref="Runspace"/> with availability-driven state
/// synchronization and health tracking.
/// </summary>
public sealed class ManagedRunspace : IDisposable
{
    private readonly VariableBroadcaster _broadcaster;
    private readonly RunspaceComposerOptions _options;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private int _consecutiveSyncFailures;
    private int _isSyncing;
    private int _invocationCount;
    private long _lastUsedTicks;

    /// <summary>
    /// The underlying runspace.
    /// </summary>
    public Runspace Runspace { get; }

    /// <summary>
    /// The broadcaster generation this runspace last successfully synced to.
    /// </summary>
    public long LastSyncedGeneration { get; private set; }

    /// <summary>
    /// Current health classification.
    /// </summary>
    public RunspaceHealthState HealthState { get; private set; }

    /// <summary>
    /// Whether a sync operation is currently in progress on this runspace.
    /// </summary>
    public bool IsSyncing => Volatile.Read(ref _isSyncing) != 0;

    /// <summary>
    /// Total number of AvailabilityChanged transitions observed (approximates invocation count).
    /// </summary>
    public int InvocationCount => Volatile.Read(ref _invocationCount);

    /// <summary>
    /// UTC timestamp of the last observed activity on this runspace.
    /// </summary>
    public DateTime LastUsedUtc => new(Volatile.Read(ref _lastUsedTicks), DateTimeKind.Utc);

    internal ManagedRunspace(
        Runspace runspace,
        VariableBroadcaster broadcaster,
        RunspaceComposerOptions options)
    {
        Runspace = runspace;
        _broadcaster = broadcaster;
        _options = options;
        _lastUsedTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// Subscribe to the runspace's <see cref="Runspace.AvailabilityChanged"/> event.
    /// </summary>
    internal void AttachHandlers()
    {
        Runspace.AvailabilityChanged += OnAvailabilityChanged;
    }

    /// <summary>
    /// Unsubscribe from the runspace's events.
    /// </summary>
    internal void DetachHandlers()
    {
        Runspace.AvailabilityChanged -= OnAvailabilityChanged;
    }

    private void OnAvailabilityChanged(object? sender, RunspaceAvailabilityEventArgs e)
    {
        if (e.RunspaceAvailability != RunspaceAvailability.Available)
        {
            return;
        }

        if (_options.TrackUsageStatistics)
        {
            Interlocked.Increment(ref _invocationCount);
            Volatile.Write(ref _lastUsedTicks, DateTime.UtcNow.Ticks);
        }

        // Non-blocking: skip if another sync is already in progress
        if (!_syncLock.Wait(0))
        {
            return;
        }

        try
        {
            if (_broadcaster.CurrentGeneration <= LastSyncedGeneration)
            {
                return;
            }

            Volatile.Write(ref _isSyncing, 1);
            TrySync();
        }
        finally
        {
            Volatile.Write(ref _isSyncing, 0);
            _syncLock.Release();
        }
    }

    private void TrySync()
    {
        var delta = _broadcaster.GetDelta(LastSyncedGeneration);

        if (delta.Items.Count == 0)
        {
            LastSyncedGeneration = delta.ToGeneration;
            return;
        }

        bool allSucceeded = true;
        long highestApplied = LastSyncedGeneration;

        foreach (var item in delta.Items)
        {
            if (!TryApplyItem(item))
            {
                allSucceeded = false;
                // ATAP: continue with remaining items
            }
        }

        if (allSucceeded)
        {
            LastSyncedGeneration = delta.ToGeneration;
            _consecutiveSyncFailures = 0;
            HealthState = RunspaceHealthState.Healthy;
        }
        else
        {
            _consecutiveSyncFailures++;

            if (_consecutiveSyncFailures >= _options.MaxSyncRetries)
            {
                HealthState = RunspaceHealthState.Faulted;
            }
            else
            {
                HealthState = RunspaceHealthState.Degraded;
            }

            // Still advance generation to avoid re-applying items that succeeded.
            // The failed items will be retried on next sync via full snapshot fallback
            // if the delta log gets compacted.
            LastSyncedGeneration = delta.ToGeneration;
        }
    }

    /// <summary>
    /// Apply a single sync item to the runspace. Returns true on success.
    /// </summary>
    private bool TryApplyItem(SyncItem item)
    {
        try
        {
            switch (item.Kind)
            {
                case SyncItemKind.Variable:
                    Runspace.SessionStateProxy.SetVariable(item.Name, item.Value);
                    return true;

                case SyncItemKind.Function:
                    return InvokeMicroPipeline(
                        $"function {item.Name} {{ {item.Value} }}");

                case SyncItemKind.Alias:
                    return InvokeMicroPipeline(
                        $"Set-Alias -Name '{EscapeSingleQuote(item.Name)}' -Value '{EscapeSingleQuote(item.Value as string ?? string.Empty)}'");

                case SyncItemKind.EnvironmentVariable:
                    return InvokeMicroPipeline(
                        $"$env:{item.Name} = $args[0]",
                        item.Value);

                case SyncItemKind.WorkingDirectory:
                    return InvokeMicroPipeline(
                        $"Set-Location -LiteralPath $args[0]",
                        item.Value);

                default:
                    return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Execute a lightweight script on this runspace synchronously.
    /// The runspace must be in <see cref="RunspaceAvailability.Available"/> state.
    /// </summary>
    private bool InvokeMicroPipeline(string script, object? argument = null)
    {
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.Runspace = Runspace;
        ps.AddScript(script);

        if (argument is not null)
        {
            ps.AddArgument(argument);
        }

        ps.Invoke();

        return !ps.HadErrors;
    }

    private static string EscapeSingleQuote(string value)
    {
        return value.Replace("'", "''");
    }

    public void Dispose()
    {
        DetachHandlers();
        _syncLock.Dispose();
    }
}
