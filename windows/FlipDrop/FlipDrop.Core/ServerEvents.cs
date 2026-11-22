namespace FlipDrop.Core;

/// <summary>Raised when an upload begins (after the type is sniffed from the first bytes).</summary>
public sealed class TransferStartedEventArgs : EventArgs
{
    public required Guid TransferId { get; init; }
    public required string DisplayName { get; init; }
    public required FileKind Kind { get; init; }
    public required long TotalBytes { get; init; }
    public required string DeviceName { get; init; }
    public double? FlingX { get; init; }
    public double? FlingVelocity { get; init; }
}

/// <summary>Progress of a running upload. Throttled to at most 5 per second per transfer.</summary>
public sealed class TransferProgressEventArgs : EventArgs
{
    public required Guid TransferId { get; init; }
    public required long BytesReceived { get; init; }
    public required long TotalBytes { get; init; }
}

/// <summary>Raised once a file has been fully received and atomically moved into the save folder.</summary>
public sealed class FileReceivedEventArgs : EventArgs
{
    public required Guid TransferId { get; init; }
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required FileKind Kind { get; init; }
    public required long SizeBytes { get; init; }
    public required bool IsRisky { get; init; }
    public required string DeviceName { get; init; }
    public double? FlingX { get; init; }
    public double? FlingVelocity { get; init; }
}

/// <summary>Raised when an upload fails (aborted, short body, or an error while writing).</summary>
public sealed class TransferFailedEventArgs : EventArgs
{
    public required Guid TransferId { get; init; }
    public required string Reason { get; init; }
}

/// <summary>Raised when pairing is locked out after too many wrong codes.</summary>
public sealed class PairingLockedEventArgs : EventArgs
{
    public required TimeSpan LockedFor { get; init; }
}
