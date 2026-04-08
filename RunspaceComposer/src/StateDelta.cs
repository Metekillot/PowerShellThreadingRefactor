// Copyright (c) Joshua Kidder. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace Microsoft.PowerShell.RunspaceComposer;

/// <summary>
/// Represents the set of state changes between two broadcaster generations.
/// </summary>
public sealed class StateDelta
{
    /// <summary>
    /// The generation this delta starts from (exclusive).
    /// </summary>
    public long FromGeneration { get; }

    /// <summary>
    /// The generation this delta brings a runspace up to (inclusive).
    /// </summary>
    public long ToGeneration { get; }

    /// <summary>
    /// The changed items in this delta. Later items for the same name supersede earlier ones.
    /// </summary>
    public IReadOnlyList<SyncItem> Items { get; }

    public StateDelta(long fromGeneration, long toGeneration, IReadOnlyList<SyncItem> items)
    {
        FromGeneration = fromGeneration;
        ToGeneration = toGeneration;
        Items = items;
    }
}
