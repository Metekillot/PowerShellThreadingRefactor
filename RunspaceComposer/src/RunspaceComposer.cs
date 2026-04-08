// Copyright (c) Joshua Kidder. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation.Runspaces;

namespace Microsoft.PowerShell.RunspaceComposer;

/// <summary>
/// Aggregate statistics for a <see cref="RunspaceComposer"/> instance.
/// </summary>
public sealed record RunspaceComposerStatistics
{
    public int TotalManagedRunspaces { get; init; }
    public int HealthyCount { get; init; }
    public int DegradedCount { get; init; }
    public int FaultedCount { get; init; }
    public long BroadcasterGeneration { get; init; }
    public long MinSyncedGeneration { get; init; }
    public long MaxSyncedGeneration { get; init; }
}

/// <summary>
/// Wraps a <see cref="RunspacePool"/> with per-runspace lifecycle tracking
/// and a <see cref="VariableBroadcaster"/> for ongoing state synchronization.
/// </summary>
/// <remarks>
/// <para>
/// Each runspace created by the pool is automatically wrapped in a
/// <see cref="ManagedRunspace"/> that subscribes to
/// <see cref="Runspace.AvailabilityChanged"/>. When a runspace transitions
/// to <see cref="RunspaceAvailability.Available"/> (idle), it pulls any
/// pending state changes from the broadcaster.
/// </para>
/// </remarks>
public sealed class RunspaceComposer : IDisposable
{
    private readonly RunspacePool _pool;
    private readonly VariableBroadcaster _broadcaster;
    private readonly ConcurrentDictionary<int, ManagedRunspace> _managedRunspaces = new();
    private readonly RunspaceComposerOptions _options;
    private bool _disposed;

    /// <summary>
    /// The broadcaster used by this composer. Exposed for direct access
    /// to snapshot/delta queries beyond the <see cref="Publish"/> convenience methods.
    /// </summary>
    public VariableBroadcaster Broadcaster => _broadcaster;

    /// <summary>
    /// The underlying <see cref="RunspacePool"/>.
    /// </summary>
    public RunspacePool Pool => _pool;

    /// <summary>
    /// Initializes a new <see cref="RunspaceComposer"/> wrapping the given pool.
    /// </summary>
    /// <param name="pool">
    /// A local <see cref="RunspacePool"/>. Must not be remote.
    /// The pool may be in any state (before open, opened, etc.) — the composer
    /// will begin tracking runspaces as they are created.
    /// </param>
    /// <param name="options">Optional configuration. Uses defaults if null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="pool"/> is a remote pool.</exception>
    public RunspaceComposer(RunspacePool pool, RunspaceComposerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pool);

        if (pool.ConnectionInfo is not null)
        {
            throw new InvalidOperationException(
                "RunspaceComposer only supports local RunspacePools. Remote pools are not supported.");
        }

        _pool = pool;
        _options = options ?? new RunspaceComposerOptions();
        _broadcaster = new VariableBroadcaster();

        _pool.RunspaceCreated += OnRunspaceCreated;
    }

    /// <summary>
    /// Publish a single state item to be synchronized across all managed runspaces
    /// on their next idle transition.
    /// </summary>
    /// <returns>The new broadcaster generation.</returns>
    public long Publish(string name, object? value, SyncItemKind kind = SyncItemKind.Variable)
    {
        return _broadcaster.Publish(name, value, kind);
    }

    /// <summary>
    /// Publish a batch of state items atomically under a single generation increment.
    /// </summary>
    /// <returns>The new broadcaster generation.</returns>
    public long PublishBatch(IEnumerable<SyncItem> items)
    {
        return _broadcaster.PublishBatch(items);
    }

    /// <summary>
    /// Returns a snapshot of all currently tracked managed runspaces.
    /// </summary>
    public IReadOnlyCollection<ManagedRunspace> GetManagedRunspaces()
    {
        return _managedRunspaces.Values.ToList();
    }

    /// <summary>
    /// Returns a <see cref="ManagedRunspace"/> for the given runspace, if tracked.
    /// </summary>
    public ManagedRunspace? GetManagedRunspace(Runspace runspace)
    {
        _managedRunspaces.TryGetValue(runspace.Id, out var managed);
        return managed;
    }

    /// <summary>
    /// Compact the broadcaster's delta log up to the minimum synced generation
    /// across all healthy runspaces.
    /// </summary>
    public void CompactDeltas()
    {
        long minGen = long.MaxValue;
        bool hasHealthy = false;

        foreach (var managed in _managedRunspaces.Values)
        {
            if (managed.HealthState != RunspaceHealthState.Faulted)
            {
                hasHealthy = true;
                long gen = managed.LastSyncedGeneration;
                if (gen < minGen)
                {
                    minGen = gen;
                }
            }
        }

        if (hasHealthy && minGen > 0 && minGen < long.MaxValue)
        {
            _broadcaster.CompactDeltasBefore(minGen);
        }
    }

    /// <summary>
    /// Returns aggregate statistics for this composer.
    /// </summary>
    public RunspaceComposerStatistics GetStatistics()
    {
        int total = 0, healthy = 0, degraded = 0, faulted = 0;
        long minGen = long.MaxValue, maxGen = long.MinValue;

        foreach (var managed in _managedRunspaces.Values)
        {
            total++;
            switch (managed.HealthState)
            {
                case RunspaceHealthState.Healthy:
                    healthy++;
                    break;
                case RunspaceHealthState.Degraded:
                    degraded++;
                    break;
                case RunspaceHealthState.Faulted:
                    faulted++;
                    break;
            }

            long gen = managed.LastSyncedGeneration;
            if (gen < minGen) minGen = gen;
            if (gen > maxGen) maxGen = gen;
        }

        return new RunspaceComposerStatistics
        {
            TotalManagedRunspaces = total,
            HealthyCount = healthy,
            DegradedCount = degraded,
            FaultedCount = faulted,
            BroadcasterGeneration = _broadcaster.CurrentGeneration,
            MinSyncedGeneration = total > 0 ? minGen : 0,
            MaxSyncedGeneration = total > 0 ? maxGen : 0,
        };
    }

    private void OnRunspaceCreated(object? sender, RunspaceCreatedEventArgs e)
    {
        var managed = new ManagedRunspace(e.Runspace, _broadcaster, _options);
        managed.AttachHandlers();

        _managedRunspaces[e.Runspace.Id] = managed;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _pool.RunspaceCreated -= OnRunspaceCreated;

        foreach (var managed in _managedRunspaces.Values)
        {
            managed.Dispose();
        }

        _managedRunspaces.Clear();
    }
}
