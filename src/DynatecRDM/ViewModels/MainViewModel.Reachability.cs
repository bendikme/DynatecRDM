using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DynatecRDM.Converters;
using DynatecRDM.Resources;
using DynatecRDM.Services;

namespace DynatecRDM.ViewModels;

/// <summary>
/// Whether connections that are not running would answer: a mark in the tree and a line in the
/// detail pane. The hosts are only asked while the window is on screen.
/// </summary>
public sealed partial class MainViewModel
{
    private IDisposable? _reachDemand;
    private bool _reachSubscribed;
    private Reachability _detailReach;
    private string _detailReachText = string.Empty;
    private string _detailReachHint = string.Empty;
    private bool _detailReachChecking;
    private bool _showDetailReach;
    private ICommand? _checkReachabilityCommand;

    /// <summary>Whether the selected connection's host answered when last asked.</summary>
    public Reachability DetailReach
    {
        get => _detailReach;
        private set => SetProperty(ref _detailReach, value);
    }

    /// <summary>"Answers on port 3389 · checked just now", or what a check found instead.</summary>
    public string DetailReachText
    {
        get => _detailReachText;
        private set => SetProperty(ref _detailReachText, value);
    }

    /// <summary>What an unanswered check can mean; empty otherwise.</summary>
    public string DetailReachHint
    {
        get => _detailReachHint;
        private set => SetProperty(ref _detailReachHint, value);
    }

    public bool DetailReachChecking
    {
        get => _detailReachChecking;
        private set => SetProperty(ref _detailReachChecking, value);
    }

    /// <summary>The line shows for a connection that can be checked and is not running.</summary>
    public bool ShowDetailReach
    {
        get => _showDetailReach;
        private set => SetProperty(ref _showDetailReach, value);
    }

    public ICommand CheckReachabilityCommand =>
        _checkReachabilityCommand ??= new AsyncRelayCommand(_ => CheckSelectedReachabilityAsync());

    /// <summary>Asks for reachability while the window is visible, and stops when it is hidden.</summary>
    private void AttachReachability(Window window)
    {
        if (!_reachSubscribed)
        {
            _services.Reachability.Changed += OnReachabilityChanged;
            _reachSubscribed = true;
        }

        window.IsVisibleChanged += OnWindowVisibilityChanged;
        if (window.IsVisible) _reachDemand ??= _services.Reachability.Demand();
    }

    private void OnWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _reachDemand ??= _services.Reachability.Demand();
        }
        else
        {
            _reachDemand?.Dispose();
            _reachDemand = null;
        }
    }

    private void OnReachabilityChanged(object? sender, EventArgs e) =>
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_detached) return;
            ApplyReachability();
            RefreshDetailReach(_selectedNode, checkIfStale: false);
        }));

    /// <summary>Copies the latest results onto the connection rows.</summary>
    private void ApplyReachability()
    {
        foreach (var node in _flat)
        {
            if (!node.IsConnection) continue;

            var result = _services.Reachability.Get(node.Id);
            node.Reach = result?.State ?? Reachability.Unknown;
            node.ReachText = result is null ? string.Empty : Describe(result, withHint: true);
        }
    }

    /// <summary>Fills the detail line, and asks again when the answer is getting old.</summary>
    private void RefreshDetailReach(TreeNodeViewModel? node, bool checkIfStale)
    {
        var connection = node is { IsConnection: true } ? node.AsConnection : null;
        var checkable = connection is not null
                        && connection.Gateway.UsageMethod == Models.GatewayUsageMethod.DoNotUse
                        && !string.IsNullOrWhiteSpace(connection.Host);

        ShowDetailReach = checkable && !node!.IsRunning;
        if (!checkable)
        {
            DetailReach = Reachability.Unknown;
            DetailReachText = string.Empty;
            DetailReachHint = string.Empty;
            return;
        }

        var result = _services.Reachability.Get(node!.Id);
        DetailReach = result?.State ?? Reachability.Unknown;
        DetailReachText = result is null ? string.Empty : Describe(result, withHint: false);
        DetailReachHint = result?.State == Reachability.NoAnswer ? Strings.Reach_NoAnswer_Hint : string.Empty;

        if (checkIfStale && !node.IsRunning && !_services.Reachability.IsFresh(node.Id))
            _ = CheckSelectedReachabilityAsync();
    }

    private async Task CheckSelectedReachabilityAsync()
    {
        if (_selectedNode is not { IsConnection: true, AsConnection: { } connection } || DetailReachChecking) return;

        DetailReachChecking = true;
        try
        {
            await _services.Reachability.CheckAsync(connection).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Checking {connection.Host} failed: {ex.Message}");
        }
        finally
        {
            DetailReachChecking = false;
        }
    }

    private static string Describe(ReachabilityResult result, bool withHint)
    {
        var when = RelativeTimeConverter.Describe(result.CheckedUtc);
        if (result.State == Reachability.Responds)
            return UiLanguage.Format(Strings.Reach_Responds_Detail, result.Port, when);

        var text = UiLanguage.Format(Strings.Reach_NoAnswer_Detail, result.Port, when);
        return withHint ? text + Environment.NewLine + Strings.Reach_NoAnswer_Hint : text;
    }

    private void DetachReachability()
    {
        try
        {
            _reachDemand?.Dispose();
            _reachDemand = null;
            if (_window is not null) _window.IsVisibleChanged -= OnWindowVisibilityChanged;
            if (_reachSubscribed) _services.Reachability.Changed -= OnReachabilityChanged;
            _reachSubscribed = false;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not detach the reachability checks.", ex);
        }
    }
}
