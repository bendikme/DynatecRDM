using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using DynatecRDM.Models;
using DynatecRDM.Resources;

namespace DynatecRDM.ViewModels;

/// <summary>What the session bar asks of whoever owns the session windows.</summary>
public interface ISessionBarHost
{
    void SwitchTo(RdpSession session);
    void Minimize(RdpSession session);
    void ExitFullScreen(RdpSession session);

    /// <summary>Takes a session's window full screen - offered when the bar is over a window without a frame.</summary>
    void EnterFullScreen(RdpSession session) { }

    /// <summary>Shows the frame of a window without one, so it can be moved or resized.</summary>
    void ShowFrame(RdpSession session) { }

    void Disconnect(RdpSession session);
    void LaunchAnother();
    void OpenManager();
}

/// <summary>One running connection on the session bar.</summary>
public sealed class SessionBarTab : ObservableObject
{
    private bool _isCurrent;

    public SessionBarTab(RdpSession session) => Session = session;

    public RdpSession Session { get; }

    /// <summary>The session the bar is shown over, the one the right-hand buttons act on.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }
}

/// <summary>
/// Backs the session bar: one tab per running connection, in the order they were started, and
/// the actions for the session in front. Disconnecting asks twice when the user wants closes
/// confirmed; the second click has to come within a few seconds.
/// </summary>
public sealed class SessionBarViewModel : ObservableObject
{
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(3);

    private readonly ISessionBarHost _host;
    private readonly Func<bool> _confirmCloses;
    private readonly DispatcherTimer _confirmTimer;
    private readonly RelayCommand _minimizeCommand;
    private readonly RelayCommand _exitFullScreenCommand;
    private readonly RelayCommand _enterFullScreenCommand;
    private readonly RelayCommand _showFrameCommand;
    private readonly RelayCommand _disconnectCommand;

    private SessionBarTab? _current;
    private bool _isConfirmingDisconnect;
    private bool _isOverWindow;

    public SessionBarViewModel(ISessionBarHost host, Func<bool> confirmCloses)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _confirmCloses = confirmCloses ?? throw new ArgumentNullException(nameof(confirmCloses));

        _confirmTimer = new DispatcherTimer { Interval = ConfirmWindow };
        _confirmTimer.Tick += (_, _) => ResetConfirm();

        SwitchCommand = new RelayCommand(p =>
        {
            if (p is SessionBarTab tab && Tabs.Contains(tab) && tab.Session.IsActive) _host.SwitchTo(tab.Session);
        });
        LaunchCommand = new RelayCommand(_host.LaunchAnother);
        OpenManagerCommand = new RelayCommand(_host.OpenManager);

        _minimizeCommand = new RelayCommand(() => Act(_host.Minimize), () => _current is not null);
        _exitFullScreenCommand = new RelayCommand(() => Act(_host.ExitFullScreen), () => _current is not null);
        _enterFullScreenCommand = new RelayCommand(() => Act(_host.EnterFullScreen), () => _current is not null);
        _showFrameCommand = new RelayCommand(() => Act(_host.ShowFrame), () => _current is not null);
        _disconnectCommand = new RelayCommand(Disconnect, () => _current is not null);
    }

    public ObservableCollection<SessionBarTab> Tabs { get; } = new();

    public ICommand SwitchCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand OpenManagerCommand { get; }
    public ICommand MinimizeCommand => _minimizeCommand;
    public ICommand ExitFullScreenCommand => _exitFullScreenCommand;
    public ICommand EnterFullScreenCommand => _enterFullScreenCommand;
    public ICommand ShowFrameCommand => _showFrameCommand;
    public ICommand DisconnectCommand => _disconnectCommand;

    /// <summary>
    /// The bar hangs from a window without a frame rather than a full-screen monitor: it offers
    /// full screen and the frame instead of leaving full screen.
    /// </summary>
    public bool IsOverWindow
    {
        get => _isOverWindow;
        set => SetProperty(ref _isOverWindow, value);
    }

    /// <summary>The session in front, or null when the bar is not over one of ours.</summary>
    public RdpSession? Current => _current?.Session;

    public bool IsConfirmingDisconnect
    {
        get => _isConfirmingDisconnect;
        private set
        {
            if (SetProperty(ref _isConfirmingDisconnect, value)) OnPropertyChanged(nameof(DisconnectText));
        }
    }

    public string DisconnectText => _isConfirmingDisconnect ? Strings.SessionBar_Disconnect_Confirm : Strings.SessionBar_Disconnect;

    /// <summary>
    /// Mirrors the running sessions. Tabs are kept rather than rebuilt, so a state change or a
    /// new session does not make the ones already on the bar flicker.
    /// </summary>
    public void Sync(IReadOnlyList<RdpSession> sessions)
    {
        var wanted = new List<RdpSession>(sessions.Count);
        foreach (var session in sessions)
            if (session.IsActive) wanted.Add(session);

        var unchanged = wanted.Count == Tabs.Count;
        for (var i = 0; unchanged && i < wanted.Count; i++)
            unchanged = ReferenceEquals(Tabs[i].Session, wanted[i]);
        if (unchanged) return;

        var kept = new Dictionary<Guid, SessionBarTab>(Tabs.Count);
        foreach (var tab in Tabs) kept[tab.Session.Id] = tab;

        var currentId = _current?.Session.Id;
        Tabs.Clear();
        foreach (var session in wanted)
            Tabs.Add(kept.TryGetValue(session.Id, out var tab) ? tab : new SessionBarTab(session));

        SetCurrent(currentId);
    }

    public void SetCurrent(Guid? sessionId)
    {
        SessionBarTab? next = null;
        foreach (var tab in Tabs)
        {
            var match = sessionId is { } id && tab.Session.Id == id;
            tab.IsCurrent = match;
            if (match) next = tab;
        }

        if (ReferenceEquals(next, _current)) return;

        _current = next;
        ResetConfirm();
        OnPropertyChanged(nameof(Current));
        _minimizeCommand.RaiseCanExecuteChanged();
        _exitFullScreenCommand.RaiseCanExecuteChanged();
        _enterFullScreenCommand.RaiseCanExecuteChanged();
        _showFrameCommand.RaiseCanExecuteChanged();
        _disconnectCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Moves to the next or previous session, wrapping round; the mouse wheel uses this.</summary>
    public void SwitchBy(int step)
    {
        if (Tabs.Count == 0 || step == 0) return;

        var index = _current is null ? -1 : Tabs.IndexOf(_current);
        var next = index < 0
            ? (step > 0 ? 0 : Tabs.Count - 1)
            : ((index + step) % Tabs.Count + Tabs.Count) % Tabs.Count;

        if (next != index) _host.SwitchTo(Tabs[next].Session);
    }

    public void ResetConfirm()
    {
        _confirmTimer.Stop();
        IsConfirmingDisconnect = false;
    }

    private void Act(Action<RdpSession> action)
    {
        if (_current?.Session is { IsActive: true } session) action(session);
    }

    private void Disconnect()
    {
        if (_current?.Session is not { IsActive: true } session) return;

        if (_confirmCloses() && !_isConfirmingDisconnect)
        {
            IsConfirmingDisconnect = true;
            _confirmTimer.Stop();
            _confirmTimer.Start();
            return;
        }

        ResetConfirm();
        _host.Disconnect(session);
    }
}
