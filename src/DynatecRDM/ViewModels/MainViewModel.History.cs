using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using DynatecRDM.Models;
using DynatecRDM.Resources;
using DynatecRDM.Services;

namespace DynatecRDM.ViewModels;

/// <summary>One session in the history panel, formatted for display.</summary>
public sealed class HistoryRow : ObservableObject
{
    private string _durationText = string.Empty;

    public HistoryRow(ConnectionLogEntry entry, string? viaMultiConfig, DateTime nowUtc)
    {
        Entry = entry;

        var started = entry.StartedUtc.ToLocalTime();
        StartText = started.ToString("g", UiLanguage.Culture);

        if (entry.EndedUtc is { } endedUtc && entry.Outcome != SessionOutcome.Unknown)
        {
            // Just the time, with "+1" when it ended a day later: the column stays narrow and the
            // full date is one row up anyway.
            var ended = endedUtc.ToLocalTime();
            var days = (ended.Date - started.Date).Days;
            EndText = days > 0
                ? ended.ToString("t", UiLanguage.Culture) + " +" + days.ToString(UiLanguage.Culture)
                : ended.ToString("t", UiLanguage.Culture);
        }
        else
        {
            EndText = "–";
        }

        OutcomeText = entry.Outcome switch
        {
            SessionOutcome.Open => Strings.Main_History_Outcome_Open,
            SessionOutcome.Ended => Strings.Main_History_Outcome_Ended,
            SessionOutcome.Dropped => Strings.Main_History_Outcome_Dropped,
            SessionOutcome.Failed => Strings.Main_History_Outcome_Failed,
            SessionOutcome.AppClosed => Strings.Main_History_Outcome_AppClosed,
            _ => Strings.Main_History_Outcome_Unknown,
        };

        var notes = new List<string>(2);
        if (entry.Reconnects > 0)
            notes.Add(UiLanguage.Plural(entry.Reconnects, Strings.Main_History_Reconnects_One, Strings.Main_History_Reconnects_Many));
        if (!string.IsNullOrWhiteSpace(viaMultiConfig))
            notes.Add(UiLanguage.Format(Strings.Main_History_ViaMultiConfig, viaMultiConfig));
        NoteText = string.Join(" · ", notes);

        Tick(nowUtc);
    }

    public ConnectionLogEntry Entry { get; }

    public string StartText { get; }
    public string EndText { get; }
    public string OutcomeText { get; }

    /// <summary>Reconnects and the multi-config it came from, when there is anything to say.</summary>
    public string NoteText { get; }

    public bool HasNote => NoteText.Length > 0;

    /// <summary>Why it failed or dropped, as the session manager put it.</summary>
    public string? ErrorText => string.IsNullOrWhiteSpace(Entry.Error) ? null : Entry.Error;

    public bool IsOpen => Entry.Outcome == SessionOutcome.Open;

    public bool IsProblem => Entry.Outcome is SessionOutcome.Failed or SessionOutcome.Dropped;

    public string DurationText => _durationText;

    /// <summary>Recomputes the duration; a running session's grows while the panel is open.</summary>
    public void Tick(DateTime nowUtc)
    {
        var duration = Entry.DurationAt(nowUtc);
        SetProperty(ref _durationText, duration is { } d ? MainViewModel.FormatDuration(d) : "–", nameof(DurationText));
    }
}

/// <summary>The history panel of the connection detail.</summary>
public sealed partial class MainViewModel
{
    /// <summary>How many sessions the panel lists; the store keeps more.</summary>
    private const int HistoryLimit = 200;

    private DispatcherTimer? _historyTimer;
    private bool _historySubscribed;
    private Guid? _historyConnectionId;
    private int _historyVersion;
    private string _historyTotalTime = string.Empty;
    private bool _hasHistory;
    private bool _clearingHistory;

    public ObservableCollection<HistoryRow> History { get; } = new();

    /// <summary>Removes the selected connection's finished sessions, after asking.</summary>
    public ICommand ClearHistoryCommand { get; }

    /// <summary>There is something to clear: a finished session. A running one is never cleared.</summary>
    private bool CanClearHistory =>
        !_clearingHistory && _historyConnectionId is not null && History.Any(static row => !row.IsOpen);

    public bool HasHistory
    {
        get => _hasHistory;
        private set => SetProperty(ref _hasHistory, value);
    }

    /// <summary>The time the listed sessions were connected, added up.</summary>
    public string HistoryTotalTime
    {
        get => _historyTotalTime;
        private set => SetProperty(ref _historyTotalTime, value);
    }

    /// <summary>"5 s", "12 min", "2 t 5 min", "3 d 4 t" - the largest two units that matter.</summary>
    public static string FormatDuration(TimeSpan span)
    {
        if (span.Ticks < 0) span = TimeSpan.Zero;

        return span.TotalSeconds < 60 ? UiLanguage.Format(Strings.Main_History_Duration_Seconds, (int)span.TotalSeconds)
            : span.TotalMinutes < 60 ? UiLanguage.Format(Strings.Main_History_Duration_Minutes, (int)span.TotalMinutes)
            : span.TotalHours < 24 ? UiLanguage.Format(Strings.Main_History_Duration_Hours, (int)span.TotalHours, span.Minutes)
            : UiLanguage.Format(Strings.Main_History_Duration_Days, (int)span.TotalDays, span.Hours);
    }

    /// <summary>Shows the history of the selected connection, or clears the panel for anything else.</summary>
    private void RefreshHistory(TreeNodeViewModel? node)
    {
        if (_detached) return;

        if (!_historySubscribed)
        {
            _services.History.Changed += OnHistoryChanged;
            _historySubscribed = true;
        }

        Guid? id = node is { IsConnection: true } ? node.Id : null;
        if (id != _historyConnectionId)
        {
            History.Clear();
            HasHistory = false;
            HistoryTotalTime = string.Empty;
            _historyConnectionId = id;
            ((AsyncRelayCommand)ClearHistoryCommand).RaiseCanExecuteChanged();
        }

        if (id is { } connectionId) _ = LoadHistoryAsync(connectionId, ++_historyVersion);
        else UpdateHistoryTimer();
    }

    private async Task LoadHistoryAsync(Guid connectionId, int version)
    {
        var entries = await _services.History.GetAsync(connectionId, HistoryLimit).ConfigureAwait(true);

        // A newer selection or a newer load got there first.
        if (_detached || version != _historyVersion || _historyConnectionId != connectionId) return;

        var now = DateTime.UtcNow;
        var total = TimeSpan.Zero;

        History.Clear();
        foreach (var entry in entries)
        {
            string? via = null;
            if (entry.MultiConfigId is { } multiId && _index.TryGetValue(multiId, out var multi)) via = multi.Name;

            History.Add(new HistoryRow(entry, via, now));
            if (entry.DurationAt(now) is { } duration) total += duration;
        }

        HasHistory = History.Count > 0;
        HistoryTotalTime = HasHistory ? FormatDuration(total) : string.Empty;
        ((AsyncRelayCommand)ClearHistoryCommand).RaiseCanExecuteChanged();
        UpdateHistoryTimer();
    }

    private async Task ClearHistoryAsync()
    {
        if (_historyConnectionId is not { } connectionId || !CanClearHistory) return;

        var name = _selectedNode is { IsConnection: true } node && node.Id == connectionId ? node.Name : string.Empty;
        if (!Confirm(
                Strings.Main_History_Clear,
                UiLanguage.Format(Strings.Main_History_Clear_Message, name),
                Strings.Main_History_Clear_Confirm)) return;

        await ClearHistoryConfirmedAsync(connectionId).ConfigureAwait(true);
    }

    /// <summary>The clear itself, once the user has said yes; the list then shows what is left.</summary>
    private async Task ClearHistoryConfirmedAsync(Guid connectionId)
    {
        _clearingHistory = true;
        ((AsyncRelayCommand)ClearHistoryCommand).RaiseCanExecuteChanged();
        try
        {
            var removed = await _services.History.ClearAsync(connectionId).ConfigureAwait(true);
            AppLog.Info($"Cleared {removed} session(s) from a connection's history.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not clear the connection history.", ex);
            Notify(UiLanguage.Format(Strings.Main_Error_ClearHistory, ex.Message), true);
        }
        finally
        {
            _clearingHistory = false;
        }

        if (_detached || _historyConnectionId != connectionId)
        {
            ((AsyncRelayCommand)ClearHistoryCommand).RaiseCanExecuteChanged();
            return;
        }

        await LoadHistoryAsync(connectionId, ++_historyVersion).ConfigureAwait(true);
    }

    /// <summary>Raised from the history writer's thread whenever a session was saved.</summary>
    private void OnHistoryChanged(object? sender, Guid connectionId)
    {
        // BeginInvoke, never Invoke: the app's shutdown waits on the writer from the UI thread.
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_detached || _historyConnectionId != connectionId) return;
            _ = LoadHistoryAsync(connectionId, ++_historyVersion);
        }));
    }

    /// <summary>Ticks only while a listed session is still running.</summary>
    private void UpdateHistoryTimer()
    {
        var anyOpen = false;
        foreach (var row in History)
        {
            if (row.IsOpen) { anyOpen = true; break; }
        }

        if (!anyOpen)
        {
            _historyTimer?.Stop();
            return;
        }

        if (_historyTimer is null)
        {
            _historyTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _historyTimer.Tick += OnHistoryTick;
        }
        _historyTimer.Start();
    }

    private void OnHistoryTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var total = TimeSpan.Zero;
        foreach (var row in History)
        {
            if (row.IsOpen) row.Tick(now);
            if (row.Entry.DurationAt(now) is { } duration) total += duration;
        }
        HistoryTotalTime = FormatDuration(total);
    }

    private void DetachHistory()
    {
        try
        {
            if (_historySubscribed) _services.History.Changed -= OnHistoryChanged;
            _historySubscribed = false;

            if (_historyTimer is not null)
            {
                _historyTimer.Stop();
                _historyTimer.Tick -= OnHistoryTick;
                _historyTimer = null;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not detach the history panel.", ex);
        }
    }
}
