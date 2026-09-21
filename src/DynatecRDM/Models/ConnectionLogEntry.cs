namespace DynatecRDM.Models;

/// <summary>How a logged session came to an end.</summary>
public enum SessionOutcome
{
    /// <summary>Still running, as far as this run of the app knows.</summary>
    Open = 0,
    /// <summary>Closed normally - from the app, or by leaving the remote session.</summary>
    Ended = 1,
    /// <summary>Was connected, then dropped and could not be brought back.</summary>
    Dropped = 2,
    /// <summary>Never got as far as a session window.</summary>
    Failed = 3,
    /// <summary>Still running when the app closed. Sessions outlive the app, so the real end is unknown.</summary>
    AppClosed = 4,
    /// <summary>The app stopped without recording an end - a crash or a power cut.</summary>
    Unknown = 5,
}

/// <summary>
/// One session in a connection's history: from the moment it was started to the moment it ended.
/// A session the watchdog brings back after a drop stays one entry, with its reconnects counted.
/// </summary>
public sealed class ConnectionLogEntry
{
    /// <summary>The session's own id, so the start and the end of one session update one row.</summary>
    public Guid Id { get; set; }

    public Guid ConnectionId { get; set; }

    /// <summary>The multi-config the session was started from, if any.</summary>
    public Guid? MultiConfigId { get; set; }

    /// <summary>The address at the time; the connection may point somewhere else later.</summary>
    public string Host { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    /// <summary>When the session window first appeared. Null for a session that never connected.</summary>
    public DateTime? ConnectedUtc { get; set; }

    public DateTime? EndedUtc { get; set; }

    public SessionOutcome Outcome { get; set; }

    /// <summary>How many times the watchdog had to bring the session back.</summary>
    public int Reconnects { get; set; }

    /// <summary>Why a failed or dropped session ended, in the words the session manager logged.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// How long the session was usable: from connecting to ending, or to <paramref name="nowUtc"/>
    /// while it is still open. Null when it never connected or its end was never recorded.
    /// </summary>
    public TimeSpan? DurationAt(DateTime nowUtc)
    {
        if (ConnectedUtc is not { } connected) return null;

        var end = EndedUtc ?? (Outcome == SessionOutcome.Open ? nowUtc : (DateTime?)null);
        if (end is not { } stop) return null;

        return stop > connected ? stop - connected : TimeSpan.Zero;
    }

    public ConnectionLogEntry Clone() => (ConnectionLogEntry)MemberwiseClone();
}
