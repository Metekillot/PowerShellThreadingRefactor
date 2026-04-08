// Copyright (c) Joshua Kidder. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace Microsoft.PowerShell.RunspaceComposer;

/// <summary>
/// Thread-safe, versioned state hub that <see cref="ManagedRunspace"/> instances
/// poll for updates during idle transitions.
/// </summary>
/// <remarks>
/// <para>
/// All reads are lock-free. Writes use <see cref="Interlocked"/> and
/// <see cref="ImmutableInterlocked"/> CAS operations.
/// </para>
/// <para>
/// Each mutation increments a monotonic generation counter. Consumers track
/// their last-synced generation and request a <see cref="StateDelta"/> covering
/// only the gap.
/// </para>
/// </remarks>
public sealed class VariableBroadcaster
{
    private long _generation;
    private ImmutableDictionary<string, SyncItem> _currentState = ImmutableDictionary<string, SyncItem>.Empty;
    private readonly ConcurrentDictionary<long, StateDelta> _deltaLog = new();

    /// <summary>
    /// The current generation number. Monotonically increasing.
    /// </summary>
    public long CurrentGeneration => Volatile.Read(ref _generation);

    /// <summary>
    /// Publish a single state item. Thread-safe from any thread.
    /// </summary>
    /// <returns>The new generation number after this publication.</returns>
    public long Publish(string name, object? value, SyncItemKind kind = SyncItemKind.Variable)
    {
        var item = new SyncItem(name, value, kind);
        return PublishCore(new[] { item });
    }

    /// <summary>
    /// Publish a batch of state items atomically under a single generation increment.
    /// </summary>
    /// <returns>The new generation number after this publication.</returns>
    public long PublishBatch(IEnumerable<SyncItem> items)
    {
        var itemList = items as IReadOnlyList<SyncItem> ?? items.ToList();
        if (itemList.Count == 0)
        {
            return CurrentGeneration;
        }

        return PublishCore(itemList);
    }

    /// <summary>
    /// Get the composed delta from <paramref name="fromGeneration"/> to the current generation.
    /// </summary>
    /// <remarks>
    /// If intermediate deltas have been compacted away, falls back to a full-snapshot diff
    /// against the current state.
    /// </remarks>
    public StateDelta GetDelta(long fromGeneration)
    {
        long current = CurrentGeneration;

        if (fromGeneration >= current)
        {
            return new StateDelta(fromGeneration, current, Array.Empty<SyncItem>());
        }

        // Try to compose from delta log
        var composedItems = new Dictionary<string, SyncItem>();
        bool complete = true;

        for (long gen = fromGeneration + 1; gen <= current; gen++)
        {
            if (_deltaLog.TryGetValue(gen, out var delta))
            {
                foreach (var item in delta.Items)
                {
                    // Later items for the same key supersede earlier ones
                    composedItems[DeltaKey(item)] = item;
                }
            }
            else
            {
                // Delta was compacted; fall back to full snapshot diff
                complete = false;
                break;
            }
        }

        if (!complete)
        {
            return BuildSnapshotDelta(fromGeneration, current);
        }

        return new StateDelta(fromGeneration, current, composedItems.Values.ToList());
    }

    /// <summary>
    /// Returns an immutable snapshot of all current state.
    /// </summary>
    public IReadOnlyDictionary<string, SyncItem> GetSnapshot()
    {
        return _currentState;
    }

    /// <summary>
    /// Remove delta log entries for generations up to and including <paramref name="upToGeneration"/>.
    /// Call this when all managed runspaces have synced past a given generation.
    /// </summary>
    public void CompactDeltasBefore(long upToGeneration)
    {
        // Remove entries that no consumer needs anymore
        foreach (var key in _deltaLog.Keys)
        {
            if (key <= upToGeneration)
            {
                _deltaLog.TryRemove(key, out _);
            }
        }
    }

    private long PublishCore(IReadOnlyList<SyncItem> items)
    {
        // Update the immutable state dictionary via CAS
        ImmutableInterlocked.Update(
            ref _currentState,
            static (state, itemList) =>
            {
                var builder = state.ToBuilder();
                foreach (var item in itemList)
                {
                    builder[DeltaKey(item)] = item;
                }

                return builder.ToImmutable();
            },
            items);

        // Increment generation and record delta
        long newGen = Interlocked.Increment(ref _generation);
        var delta = new StateDelta(newGen - 1, newGen, items);
        _deltaLog[newGen] = delta;

        return newGen;
    }

    private StateDelta BuildSnapshotDelta(long fromGeneration, long toGeneration)
    {
        // When deltas are missing, return the full current state as the diff.
        // This is correct because applying the full state is idempotent.
        var snapshot = _currentState;
        var items = snapshot.Values.ToList();
        return new StateDelta(fromGeneration, toGeneration, items);
    }

    /// <summary>
    /// Produces a unique key for a sync item accounting for kind + name,
    /// so that e.g. a variable "Path" and an environment variable "Path"
    /// don't collide.
    /// </summary>
    private static string DeltaKey(SyncItem item) => $"{item.Kind}:{item.Name}";
}
