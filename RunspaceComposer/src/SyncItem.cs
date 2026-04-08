// Copyright (c) Joshua Kidder. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.PowerShell.RunspaceComposer;

/// <summary>
/// Describes the kind of session state a <see cref="SyncItem"/> targets.
/// </summary>
public enum SyncItemKind
{
    Variable,
    Function,
    Alias,
    EnvironmentVariable,
    WorkingDirectory,
}

/// <summary>
/// A single unit of state to be synchronized across managed runspaces.
/// </summary>
/// <param name="Name">The name of the state item (variable name, function name, etc.).</param>
/// <param name="Value">
/// The value to set. For <see cref="SyncItemKind.Function"/> this is the function body as a string.
/// For <see cref="SyncItemKind.WorkingDirectory"/> this is the path as a string.
/// </param>
/// <param name="Kind">The kind of session state this item targets.</param>
public sealed record SyncItem(string Name, object? Value, SyncItemKind Kind);
