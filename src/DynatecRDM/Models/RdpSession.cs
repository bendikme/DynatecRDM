using System.ComponentModel;
using System.Runtime.CompilerServices;
using DynatecRDM.Resources;
using DynatecRDM.Services;

namespace DynatecRDM.Models;

/// <summary>A live (or recently live) mstsc process managed by the application.</summary>
public sealed class RdpSession : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>The connection this session was launched from.</summary>
    public Guid ConnectionId { get; init; }

    /// <summary>Set when the session was started as part of a multi-config.</summary>
    public Guid? MultiConfigId { get; init; }

    /// <summary>Display name captured at launch time (may be an override).</summary>
    public string DisplayName { get; set; } = string.Empty;

    public string Host { get; init; } = string.Empty;

    /// <summary>The resolved display settings actually used for this launch.</summary>
    public DisplaySettings Display { get; set; } = new();

    /// <summary>Path of the generated .rdp file backing this session.</summary>
    public string RdpFilePath { get; set; } = string.Empty;

    private int _processId;
    public int ProcessId
    {
        get => _processId;
        set => Set(ref _processId, value);
    }

    private IntPtr _windowHandle;
    public IntPtr WindowHandle
    {
        get => _windowHandle;
        set => Set(ref _windowHandle, value);
    }

    private SessionState _state = SessionState.Launching;
    public SessionState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    private string? _lastError;
    public string? LastError
    {
        get => _lastError;
        set => Set(ref _lastError, value);
    }

    private int _reconnectAttempts;
    public int ReconnectAttempts
    {
        get => _reconnectAttempts;
        set => Set(ref _reconnectAttempts, value);
    }

    private string? _snapshotPath;
    /// <summary>Most recent window snapshot on disk, shown in the tray menu.</summary>
    public string? SnapshotPath
    {
        get => _snapshotPath;
        set => Set(ref _snapshotPath, value);
    }

    private DateTime? _lastSnapshotUtc;
    public DateTime? LastSnapshotUtc
    {
        get => _lastSnapshotUtc;
        set => Set(ref _lastSnapshotUtc, value);
    }

    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedUtc { get; set; }

    /// <summary>True when the user closed the session deliberately, so the watchdog stands down.</summary>
    public bool UserInitiatedClose { get; set; }

    /// <summary>Watchdog is enabled for this session.</summary>
    public bool AutoReconnect { get; set; }

    public int MaxReconnectAttempts { get; set; } = 10;
    public int ReconnectDelaySeconds { get; set; } = 5;

    public bool IsActive => State is SessionState.Launching or SessionState.Connecting
        or SessionState.Connected or SessionState.Reconnecting;

    public string StateText => State switch
    {
        SessionState.Launching => Strings.Session_State_Starting,
        SessionState.Connecting => Strings.Session_State_Connecting,
        SessionState.Connected => Strings.Session_State_Connected,
        SessionState.Reconnecting => Strings.Session_State_Reconnecting,
        SessionState.Disconnected => Strings.Session_State_Disconnected,
        SessionState.Failed => Strings.Session_State_Failed,
        _ => State.ToString(),
    };

    public TimeSpan Uptime => (EndedUtc ?? DateTime.UtcNow) - StartedUtc;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>A physical display, as the manager presents it to the user.</summary>
public sealed record MonitorInfo(
    int Index,
    string DeviceName,
    string FriendlyName,
    int Left,
    int Top,
    int Width,
    int Height,
    int WorkLeft,
    int WorkTop,
    int WorkWidth,
    int WorkHeight,
    bool IsPrimary,
    uint DpiX,
    uint DpiY)
{
    /// <summary>
    /// The id "mstsc /l" prints for this display, which is what selectedmonitors:s: holds. It is
    /// not the position in our own list: mstsc always numbers the primary display 0.
    /// </summary>
    public int MstscId { get; init; } = -1;

    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public PixelRect Bounds => new(Left, Top, Width, Height);

    /// <summary>The monitor minus the taskbar and docked toolbars; the bounds when unknown.</summary>
    public PixelRect WorkArea => WorkWidth > 0 && WorkHeight > 0
        ? new PixelRect(WorkLeft, WorkTop, WorkWidth, WorkHeight)
        : Bounds;

    public double ScaleFactor => DpiX / 96.0;
    public string ResolutionText => $"{Width} x {Height}";
    public string Label => IsPrimary
        ? UiLanguage.Format(Strings.Monitor_Label_Primary, Index + 1, FriendlyName, ResolutionText)
        : $"{Index + 1}. {FriendlyName} ({ResolutionText})";
}
