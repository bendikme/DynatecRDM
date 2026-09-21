using System.Collections.ObjectModel;
using System.Globalization;
using DynatecRDM.Resources;
using DynatecRDM.Services;

namespace DynatecRDM.ViewModels;

/// <summary>Where one step of a connection stands.</summary>
public enum ConnectStepState
{
    Pending,
    Active,
    Done,
    /// <summary>Waiting for the user - a sign-in or certificate prompt is up.</summary>
    Waiting,
    Failed,
}

/// <summary>What the connecting screen is showing.</summary>
public enum ConnectProgressMode
{
    Connecting,
    WaitingToRetry,
    Failed,
}

/// <summary>One line of the negotiation: finding the address, reaching the port, and so on.</summary>
public sealed class ConnectStep : ObservableObject
{
    private string _label;
    private string? _detail;
    private ConnectStepState _state;

    public ConnectStep(string label) => _label = label;

    public string Label { get => _label; set => SetProperty(ref _label, value); }

    public string? Detail
    {
        get => _detail;
        set { if (SetProperty(ref _detail, value)) OnPropertyChanged(nameof(HasDetail)); }
    }

    public bool HasDetail => !string.IsNullOrEmpty(_detail);

    public ConnectStepState State { get => _state; set => SetProperty(ref _state, value); }
}

/// <summary>
/// The screen a session shows until the remote desktop is there: each step of the negotiation as it
/// happens, how long the attempt has left before it is given up, the countdown to the next attempt
/// after a dropped connection, and finally what went wrong.
///
/// It decides nothing about the connection. The session manager tells it what happened, calls
/// <see cref="Tick"/> on a timer, and acts on the events it raises: the attempt ran out of time,
/// the countdown is over, or the user chose to cancel, retry or close. Time comes from the clock it
/// is given, so the logic can be exercised without waiting.
/// </summary>
public sealed class ConnectionProgressViewModel : ObservableObject
{
    public enum Prompt
    {
        Certificate,
        SignIn,
    }

    private readonly Func<DateTime> _clock;
    private readonly bool _probing;

    private ConnectProgressMode _mode;
    private string _heading = string.Empty;
    private string? _attemptText;
    private string? _previousError;
    private string? _errorText;
    private double _progress;
    private string _progressText = string.Empty;
    private bool _paused;

    private TimeSpan _timeout;
    private DateTime _startedUtc;
    private TimeSpan _pausedTotal;
    private DateTime? _pausedSinceUtc;
    private bool _certificatePrompt;
    private bool _signInPrompt;
    private bool _timedOutRaised;

    private TimeSpan _delay;
    private DateTime _countdownStartUtc;
    private bool _countdownRaised;

    /// <param name="name">The connection's name, as the heading.</param>
    /// <param name="target">The address shown under it.</param>
    /// <param name="probeTarget">
    /// What the address and port steps check - the host, or the gateway a connection always goes
    /// through - or null when the app cannot know that in advance (a gateway used only sometimes),
    /// in which case a single "connecting" step stands for both.
    /// </param>
    public ConnectionProgressViewModel(string name, string target, (string Host, int Port, bool Gateway)? probeTarget, Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        Name = name;
        Target = target;
        _probing = probeTarget is not null;

        if (probeTarget is { } probe)
        {
            Address = new ConnectStep(probe.Gateway
                ? UiLanguage.Format(Strings.Connect_Step_GatewayAddress, probe.Host)
                : Strings.Connect_Step_Address);
            Port = new ConnectStep(UiLanguage.Format(
                probe.Gateway ? Strings.Connect_Step_GatewayPort : Strings.Connect_Step_Port,
                probe.Port.ToString(CultureInfo.InvariantCulture)));
            Steps.Add(Address);
            Steps.Add(Port);
        }
        else
        {
            Address = new ConnectStep(Strings.Connect_Step_Reach);
            Port = Address;
            Steps.Add(Address);
        }

        Security = new ConnectStep(Strings.Connect_Step_Security);
        SignIn = new ConnectStep(Strings.Connect_Step_SignIn);
        Steps.Add(Security);
        Steps.Add(SignIn);

        CancelCommand = new RelayCommand(() => CancelRequested?.Invoke(this, EventArgs.Empty),
            () => _mode != ConnectProgressMode.Failed);
        ConnectNowCommand = new RelayCommand(() => FinishCountdown(),
            () => _mode == ConnectProgressMode.WaitingToRetry);
        RetryCommand = new RelayCommand(() => RetryRequested?.Invoke(this, EventArgs.Empty),
            () => _mode == ConnectProgressMode.Failed);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty),
            () => _mode == ConnectProgressMode.Failed);
    }

    // ------------------------------------------------------------------ what is shown

    public string Name { get; }

    public string Target { get; }

    public ObservableCollection<ConnectStep> Steps { get; } = new();

    public ConnectStep Address { get; }
    public ConnectStep Port { get; }
    public ConnectStep Security { get; }
    public ConnectStep SignIn { get; }

    public ConnectProgressMode Mode
    {
        get => _mode;
        private set
        {
            if (!SetProperty(ref _mode, value)) return;
            Raise(nameof(IsConnecting), nameof(IsWaitingToRetry), nameof(IsFailed), nameof(ShowsProgress), nameof(ShowsSteps));
            CancelCommand.RaiseCanExecuteChanged();
            ConnectNowCommand.RaiseCanExecuteChanged();
            RetryCommand.RaiseCanExecuteChanged();
            CloseCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsConnecting => _mode == ConnectProgressMode.Connecting;
    public bool IsWaitingToRetry => _mode == ConnectProgressMode.WaitingToRetry;
    public bool IsFailed => _mode == ConnectProgressMode.Failed;
    public bool ShowsProgress => _mode != ConnectProgressMode.Failed;

    /// <summary>
    /// The steps belong to an attempt. While waiting for the next one there is none under way, and
    /// the last attempt's steps would only suggest that something still is.
    /// </summary>
    public bool ShowsSteps => _mode != ConnectProgressMode.WaitingToRetry;

    /// <summary>What is happening, in a few words: connecting, reconnecting, lost, failed.</summary>
    public string Heading { get => _heading; private set => SetProperty(ref _heading, value); }

    /// <summary>"Attempt 2 of 10" while a dropped session is being brought back; null otherwise.</summary>
    public string? AttemptText
    {
        get => _attemptText;
        private set { if (SetProperty(ref _attemptText, value)) OnPropertyChanged(nameof(HasAttempt)); }
    }

    public bool HasAttempt => !string.IsNullOrEmpty(_attemptText);

    /// <summary>Why the previous attempt ended, while another is on its way.</summary>
    public string? PreviousError
    {
        get => _previousError;
        private set { if (SetProperty(ref _previousError, value)) OnPropertyChanged(nameof(HasPreviousError)); }
    }

    public bool HasPreviousError => !string.IsNullOrEmpty(_previousError);

    /// <summary>What went wrong, once the connection has been given up.</summary>
    public string? ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }

    /// <summary>
    /// 0 to 1: how much of the attempt's time has been used, or how much of the wait before the next
    /// attempt has passed.
    /// </summary>
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }

    /// <summary>True while the clock is stopped because a prompt is waiting for the user.</summary>
    public bool IsPaused { get => _paused; private set => SetProperty(ref _paused, value); }

    /// <summary>
    /// Something answered this attempt: the port the steps check, or the remote computer itself.
    /// An attempt that runs out of time after this stalled, rather than went unanswered.
    /// </summary>
    public bool Answered { get; private set; }

    /// <summary>The remote computer itself answered: its key arrived, or it put up a prompt.</summary>
    public bool ServerAnswered { get; private set; }

    public RelayCommand CancelCommand { get; }
    public RelayCommand ConnectNowCommand { get; }
    public RelayCommand RetryCommand { get; }
    public RelayCommand CloseCommand { get; }

    // ------------------------------------------------------------------ what it asks for

    /// <summary>The attempt used its time without connecting. Raised once per attempt.</summary>
    public event EventHandler? TimedOut;

    /// <summary>The wait before the next attempt is over, or the user chose to connect now.</summary>
    public event EventHandler? CountdownElapsed;

    public event EventHandler? CancelRequested;
    public event EventHandler? RetryRequested;
    public event EventHandler? CloseRequested;

    // ------------------------------------------------------------------ what happened

    /// <summary>
    /// A new attempt starts. <paramref name="attempt"/> is 1-based and only shown for a reconnect;
    /// <paramref name="maxAttempts"/> of 0 or less means there is no limit.
    /// </summary>
    public void BeginAttempt(TimeSpan timeout, bool reconnecting, int attempt = 0, int maxAttempts = 0, string? previousError = null)
    {
        _timeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(30);
        _startedUtc = _clock();
        _pausedTotal = TimeSpan.Zero;
        _pausedSinceUtc = null;
        _certificatePrompt = false;
        _signInPrompt = false;
        _timedOutRaised = false;
        IsPaused = false;
        Answered = false;
        ServerAnswered = false;

        foreach (var step in Steps)
        {
            step.State = ConnectStepState.Pending;
            step.Detail = null;
        }
        Steps[0].State = ConnectStepState.Active;

        Heading = reconnecting ? Strings.Connect_Heading_Reconnecting : Strings.Connect_Heading_Connecting;
        AttemptText = reconnecting && attempt > 0 ? DescribeAttempt(attempt, maxAttempts) : null;
        PreviousError = reconnecting && !string.IsNullOrWhiteSpace(previousError)
            ? UiLanguage.Format(Strings.Connect_PreviousError, previousError!.Trim())
            : null;
        ErrorText = null;
        Mode = ConnectProgressMode.Connecting;
        Tick();
    }

    /// <summary>A result from the app's own address and port check.</summary>
    public void OnProbe(ProbeStep step, string? detail)
    {
        if (_mode != ConnectProgressMode.Connecting || !_probing) return;

        switch (step)
        {
            case ProbeStep.AddressFound:
                if (Address.State is ConnectStepState.Pending or ConnectStepState.Active)
                {
                    Address.State = ConnectStepState.Done;
                    Address.Detail = detail;
                }
                if (Port.State == ConnectStepState.Pending) Port.State = ConnectStepState.Active;
                break;

            case ProbeStep.AddressFailed:
                if (Address.State is ConnectStepState.Pending or ConnectStepState.Active)
                {
                    Address.State = ConnectStepState.Failed;
                    Address.Detail = Strings.Connect_Step_Address_Failed;
                }
                break;

            case ProbeStep.PortOpen:
                Answered = true;
                if (Port.State is ConnectStepState.Pending or ConnectStepState.Active)
                {
                    Port.State = ConnectStepState.Done;
                    Port.Detail = Strings.Connect_Step_Port_Done;
                }
                if (Security.State == ConnectStepState.Pending) Security.State = ConnectStepState.Active;
                break;

            case ProbeStep.PortFailed:
                if (Port.State is ConnectStepState.Pending or ConnectStepState.Active)
                {
                    Port.State = ConnectStepState.Failed;
                    Port.Detail = Strings.Connect_Step_Port_Failed;
                }
                break;
        }
    }

    /// <summary>The server's key has arrived: the secure channel is up, the credentials are next.</summary>
    public void OnServerKey()
    {
        if (_mode != ConnectProgressMode.Connecting) return;
        Answered = ServerAnswered = true;
        CompleteUpTo(Security);
        if (SignIn.State == ConnectStepState.Pending) SignIn.State = ConnectStepState.Active;
    }

    /// <summary>A prompt of the control's own is up, or has been answered. The clock stops meanwhile.</summary>
    public void SetWaitingForUser(Prompt prompt, bool waiting)
    {
        if (prompt == Prompt.Certificate) _certificatePrompt = waiting;
        else _signInPrompt = waiting;

        var paused = _mode == ConnectProgressMode.Connecting && (_certificatePrompt || _signInPrompt);
        var now = _clock();
        if (paused && _pausedSinceUtc is null) _pausedSinceUtc = now;
        else if (!paused && _pausedSinceUtc is { } since)
        {
            _pausedTotal += now - since;
            _pausedSinceUtc = null;
        }
        IsPaused = paused;

        if (_mode != ConnectProgressMode.Connecting) return;

        // The step the prompt belongs to shows that it waits for the user, and why.
        var step = _certificatePrompt ? Security : SignIn;
        var other = ReferenceEquals(step, Security) ? SignIn : Security;
        if (other.State == ConnectStepState.Waiting)
        {
            other.State = ConnectStepState.Active;
            other.Detail = null;
        }

        if (paused)
        {
            Answered = ServerAnswered = true;
            if (ReferenceEquals(step, SignIn)) CompleteUpTo(Security);
            step.State = ConnectStepState.Waiting;
            step.Detail = _certificatePrompt ? Strings.Connect_Waiting_Certificate : Strings.Connect_Waiting_SignIn;
        }
        else if (step.State == ConnectStepState.Waiting)
        {
            step.State = ConnectStepState.Active;
            step.Detail = null;
        }

        Tick();
    }

    /// <summary>The session is up; every step is done.</summary>
    public void OnConnected()
    {
        foreach (var step in Steps)
        {
            if (step.State != ConnectStepState.Done) step.Detail = null;
            step.State = ConnectStepState.Done;
        }
        Progress = 1;
        IsPaused = false;
    }

    /// <summary>
    /// The connection dropped and will be tried again after <paramref name="delay"/>, unless the user
    /// connects at once or cancels.
    /// </summary>
    public void BeginCountdown(TimeSpan delay, int nextAttempt, int maxAttempts, string? lastError)
    {
        _delay = delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        _countdownStartUtc = _clock();
        _countdownRaised = false;
        _certificatePrompt = false;
        _signInPrompt = false;
        _pausedSinceUtc = null;
        IsPaused = false;

        Heading = Strings.Connect_Heading_Lost;
        AttemptText = DescribeAttempt(nextAttempt, maxAttempts);
        PreviousError = string.IsNullOrWhiteSpace(lastError)
            ? null
            : UiLanguage.Format(Strings.Connect_PreviousError, lastError!.Trim());
        ErrorText = null;
        Mode = ConnectProgressMode.WaitingToRetry;
        Tick();
    }

    /// <summary>The connection has been given up. The screen says why and offers to try again.</summary>
    public void Fail(string heading, string message)
    {
        foreach (var step in Steps)
        {
            if (step.State is ConnectStepState.Active or ConnectStepState.Waiting)
            {
                step.State = ConnectStepState.Failed;
                step.Detail = null;
            }
        }

        _pausedSinceUtc = null;
        IsPaused = false;
        Heading = heading;
        ErrorText = message;
        AttemptText = null;
        PreviousError = null;
        Mode = ConnectProgressMode.Failed;
    }

    /// <summary>Moves the clock on: the attempt's time, or the countdown. Call it a few times a second.</summary>
    public void Tick()
    {
        var now = _clock();

        switch (_mode)
        {
            case ConnectProgressMode.Connecting:
            {
                var paused = _pausedSinceUtc is { } since ? now - since : TimeSpan.Zero;
                var elapsed = now - _startedUtc - _pausedTotal - paused;
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

                Progress = Math.Min(1, elapsed.TotalSeconds / _timeout.TotalSeconds);
                var left = _timeout - elapsed;
                ProgressText = IsPaused
                    ? Strings.Connect_Paused
                    : UiLanguage.Format(Strings.Connect_Elapsed, Seconds(elapsed), Seconds(left < TimeSpan.Zero ? TimeSpan.Zero : left, up: true));

                if (!IsPaused && elapsed >= _timeout && !_timedOutRaised)
                {
                    _timedOutRaised = true;
                    TimedOut?.Invoke(this, EventArgs.Empty);
                }
                break;
            }

            case ConnectProgressMode.WaitingToRetry:
            {
                var left = _delay - (now - _countdownStartUtc);
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                Progress = _delay <= TimeSpan.Zero ? 1 : 1 - left.TotalSeconds / _delay.TotalSeconds;
                ProgressText = UiLanguage.Format(Strings.Connect_RetryIn, Seconds(left, up: true));
                if (left <= TimeSpan.Zero) FinishCountdown();
                break;
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private void FinishCountdown()
    {
        if (_mode != ConnectProgressMode.WaitingToRetry || _countdownRaised) return;
        _countdownRaised = true;
        CountdownElapsed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Marks every step before <paramref name="step"/> - and it - as done.</summary>
    private void CompleteUpTo(ConnectStep step)
    {
        foreach (var candidate in Steps)
        {
            if (candidate.State != ConnectStepState.Done)
            {
                // A step the app's own check thought had failed evidently did not: the control got past it.
                if (candidate.State == ConnectStepState.Failed) candidate.Detail = null;
                candidate.State = ConnectStepState.Done;
            }
            if (ReferenceEquals(candidate, step)) break;
        }
    }

    private static string DescribeAttempt(int attempt, int maxAttempts) => maxAttempts > 0
        ? UiLanguage.Format(Strings.Connect_Attempt, attempt, maxAttempts)
        : UiLanguage.Format(Strings.Connect_AttemptUnlimited, attempt);

    private static string Seconds(TimeSpan span, bool up = false) =>
        ((int)(up ? Math.Ceiling(span.TotalSeconds) : Math.Floor(span.TotalSeconds))).ToString(CultureInfo.InvariantCulture);
}
