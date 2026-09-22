using System.Globalization;
using DynatecRDM.Models;
using DynatecRDM.Resources;
using DynatecRDM.Services;

namespace DynatecRDM.ViewModels;

/// <summary>
/// One connection inside the multi-config being edited: the <see cref="MultiConfigItem"/>
/// together with the <see cref="RdpConnection"/> it points at.
///
/// Its display settings are edited with the same <see cref="DisplayEditorViewModel"/> as the
/// connection editor's Display tab, over the settings the item will actually launch with. The item
/// either follows the connection's display settings or has its own; changing anything gives it its
/// own, and only what differs from the connection is stored, so the rest keeps following it.
/// </summary>
public sealed class MultiConfigItemViewModel : ObservableObject, IDisposable
{
    private readonly MultiConfigItem _model;
    private readonly Action? _changed;

    private RdpConnection? _connection;
    private bool _useOwnDisplay;
    private bool _reloading;
    private bool _isSelected;
    private string _displayNameText;

    public MultiConfigItemViewModel(AppServices services, MultiConfigItem model, RdpConnection? connection, Action? changed)
    {
        ArgumentNullException.ThrowIfNull(services);
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _connection = connection;
        _changed = changed;
        _displayNameText = model.DisplayNameOverride ?? string.Empty;
        _useOwnDisplay = model.Display.HasAny;

        AutoReconnectOptions = new[]
        {
            new Option(AutoReconnectInherit, connection?.AutoReconnect ?? true
                ? Strings.Multi_AutoReconnect_Inherit_On
                : Strings.Multi_AutoReconnect_Inherit_Off),
            new Option(true, Strings.Multi_AutoReconnect_On),
            new Option(false, Strings.Multi_AutoReconnect_Off),
        };

        Display = new DisplayEditorViewModel(services, model.Display.ApplyTo(ConnectionDisplay), OnDisplayChanged);
    }

    /// <summary>The item this view model edits in place.</summary>
    public MultiConfigItem Model => _model;

    /// <summary>The display settings this item launches with.</summary>
    public DisplayEditorViewModel Display { get; }

    public Guid ConnectionId => _model.ConnectionId;

    private DisplaySettings ConnectionDisplay => _connection?.Display ?? new DisplaySettings();

    // ------------------------------------------------------------------ identity

    public RdpConnection? Connection => _connection;

    public bool IsMissing => _connection is null;

    public string ConnectionName => _connection?.Name ?? Strings.Multi_Item_MissingConnection;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(_model.DisplayNameOverride) ? ConnectionName : _model.DisplayNameOverride!;

    public string Host => _connection?.FullAddress ?? Strings.Multi_Item_ConnectionGone;

    public string? ColorHex => _connection?.Color;

    public int Order => _model.Order;

    /// <summary>One-based position shown in the list and on the map.</summary>
    public int Position => _model.Order + 1;

    /// <summary>Rewrites the stored order; the list renumbers 0..n-1 after every change.</summary>
    public void SetOrder(int order)
    {
        if (_model.Order == order) return;
        _model.Order = order;
        Raise(nameof(Order), nameof(Position));
    }

    /// <summary>
    /// The item being edited. Each item has a properties panel of its own and only the selected one
    /// is shown, so no form is ever re-pointed from one item to another - which would let radio
    /// groups and combo boxes write one item's values into the next.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool Enabled
    {
        get => _model.Enabled;
        set
        {
            if (_model.Enabled == value) return;
            _model.Enabled = value;
            Raise(nameof(Enabled));
            Touch();
        }
    }

    /// <summary>Raw text of the display-name box; blank means "use the connection name".</summary>
    public string DisplayNameText
    {
        get => _displayNameText;
        set
        {
            var text = value ?? string.Empty;
            if (!SetProperty(ref _displayNameText, text)) return;
            _model.DisplayNameOverride = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            Raise(nameof(DisplayName));
            Touch();
        }
    }

    /// <summary>Milliseconds to wait after this item before the next one starts.</summary>
    public int DelayMs
    {
        get => _model.DelayMs;
        set
        {
            var clamped = Math.Clamp(value, 0, 600000);
            if (_model.DelayMs == clamped) return;
            _model.DelayMs = clamped;
            Raise(nameof(DelayMs));
            Touch();
        }
    }

    /// <summary>
    /// What the credential combo binds to. <see cref="Guid.Empty"/> stands for "the connection's own",
    /// because a selector cannot select an item whose value is null.
    ///
    /// Nullable so that a combo box which momentarily has no matching item cannot push an
    /// unconvertible null at a non-nullable property and leave the binding in error. The
    /// getter never returns null.
    /// </summary>
    public Guid? CredentialSelection
    {
        get => _model.CredentialSetIdOverride ?? Guid.Empty;
        set
        {
            if (value is null) return;
            var chosen = value.Value == Guid.Empty ? (Guid?)null : value.Value;
            if (_model.CredentialSetIdOverride == chosen) return;
            _model.CredentialSetIdOverride = chosen;
            Raise(nameof(CredentialSelection));
            Touch();
        }
    }

    /// <summary>The value that stands for "like the connection" in the auto-reconnect combo.</summary>
    public const string AutoReconnectInherit = "inherit";

    /// <summary>The auto-reconnect combo's entries; the first says what the connection does.</summary>
    public IReadOnlyList<Option> AutoReconnectOptions { get; }

    /// <summary>
    /// Automatic reconnect as the combo shows it: <see cref="AutoReconnectInherit"/> follows the
    /// connection, true and false are this item's own. Anything else - a combo that momentarily
    /// has no selection - is ignored.
    /// </summary>
    public object AutoReconnectChoice
    {
        get => _model.AutoReconnectOverride.HasValue ? _model.AutoReconnectOverride.Value : AutoReconnectInherit;
        set
        {
            bool? chosen;
            switch (value)
            {
                case bool own:
                    chosen = own;
                    break;
                case string text when text == AutoReconnectInherit:
                    chosen = null;
                    break;
                default:
                    return;
            }

            if (_model.AutoReconnectOverride == chosen) return;
            _model.AutoReconnectOverride = chosen;
            Raise(nameof(AutoReconnectChoice));
            Touch();
        }
    }

    // ------------------------------------------------------------------ display

    /// <summary>
    /// True when the item has display settings of its own. Setting it back to false drops them and
    /// shows the connection's again; setting it to true keeps what is shown, ready to be changed.
    /// </summary>
    public bool UseOwnDisplay
    {
        get => _useOwnDisplay;
        set
        {
            if (value == _useOwnDisplay) return;
            _useOwnDisplay = value;

            if (!value)
            {
                _model.Display = new DisplayOverride();
                _reloading = true;
                try
                {
                    Display.Load(ConnectionDisplay.Clone());
                }
                finally
                {
                    _reloading = false;
                }
            }

            Raise(nameof(UseOwnDisplay), nameof(UseConnectionDisplay));
            Touch();
        }
    }

    public bool UseConnectionDisplay
    {
        get => !_useOwnDisplay;
        set => UseOwnDisplay = !value;
    }

    /// <summary>
    /// The display editor changed something. Only what differs from the connection is kept; and
    /// anything that differs means the item now has its own settings. The comparison is with the
    /// connection's settings as the editor would show them, so merely opening an item - or the
    /// monitors arriving - never counts as a change.
    /// </summary>
    private void OnDisplayChanged()
    {
        if (_reloading) return;

        var baseline = ConnectionDisplay.Clone();
        DisplayEditorViewModel.Normalize(baseline, Display.KnownMonitors());
        var over = DisplayOverride.Between(baseline, Display.Settings);

        if (over.HasAny && !_useOwnDisplay)
        {
            _useOwnDisplay = true;
            Raise(nameof(UseOwnDisplay), nameof(UseConnectionDisplay));
        }

        if (_useOwnDisplay) _model.Display = over;
        Touch();
    }

    // ------------------------------------------------------------------ derived

    public bool HasOverrides =>
        _useOwnDisplay || _model.CredentialSetIdOverride.HasValue
        || _model.AutoReconnectOverride.HasValue
        || !string.IsNullOrWhiteSpace(_model.DisplayNameOverride);

    /// <summary>One line describing where this item will land.</summary>
    public string Summary
    {
        get
        {
            if (IsMissing) return Strings.Multi_Item_ConnectionGone;

            var layout = Display.Layout;
            var text = layout.Kind switch
            {
                DisplayLayoutKind.FullScreen => layout.Monitor is { } monitor
                    ? UiLanguage.Format(Strings.Multi_Summary_FullScreenOnMonitor, monitor.Index + 1)
                    : Strings.Multi_Summary_FullScreenDefault,
                DisplayLayoutKind.FullScreenMultiMonitor => layout.MstscIds.Count == 0
                    ? Strings.Multi_Summary_SpanAll
                    : UiLanguage.Format(Strings.Multi_Summary_AcrossMonitors, MonitorNumbers(layout.Monitors)),
                DisplayLayoutKind.MaximizedWindow => UiLanguage.Format(
                    Strings.Multi_Summary_MaximizedOnMonitor, (layout.Monitor?.Index ?? 0) + 1),
                DisplayLayoutKind.WindowAtRectangle => UiLanguage.Format(
                    Strings.Multi_Summary_CustomRect,
                    layout.WindowRect.Width, layout.WindowRect.Height, layout.WindowRect.Left, layout.WindowRect.Top),
                _ => UiLanguage.Format(Strings.Multi_Summary_Windowed, layout.DesktopWidth, layout.DesktopHeight),
            };

            if (layout.IsFrameless) text = UiLanguage.Format(Strings.Multi_Summary_Frameless, text);
            if (Display.AlwaysOnTop) text = UiLanguage.Format(Strings.Multi_Summary_AlwaysOnTop, text);
            return text;
        }
    }

    /// <summary>The monitor this item takes over completely, or null when it does not.</summary>
    public int? FullscreenMonitor
    {
        get
        {
            var layout = Display.Layout;
            return layout.Kind == DisplayLayoutKind.FullScreen ? layout.Monitor?.Index : null;
        }
    }

    /// <summary>
    /// Where the session will be on the desktop, in desktop pixels: the monitors it fills, or its
    /// window. Null when there is nothing to draw it on.
    /// </summary>
    public PixelRect? Footprint
    {
        get
        {
            var layout = Display.Layout;
            return layout.Kind switch
            {
                DisplayLayoutKind.FullScreen or DisplayLayoutKind.FullScreenMultiMonitor => layout.FullScreenArea,
                DisplayLayoutKind.MaximizedWindow => layout.Monitor?.WorkArea,
                _ => layout.Monitors.Count == 0 && layout.Monitor is null ? null : layout.WindowRect,
            };
        }
    }

    /// <summary>
    /// What shows of this item's window, for the set's other windows to line up with. Null for full
    /// screen and maximized, whose edges are a monitor's own and snap already, and for an item whose
    /// connection is gone.
    /// </summary>
    public PixelRect? SnapBody
    {
        get
        {
            if (IsMissing) return null;
            var layout = Display.Layout;
            if (layout.Kind is not (DisplayLayoutKind.Window or DisplayLayoutKind.WindowAtRectangle)) return null;
            return layout.Monitor is null ? null : layout.VisibleRect;
        }
    }

    /// <summary>Drops every override and goes back to the connection's own settings.</summary>
    public void ClearOverrides()
    {
        _model.CredentialSetIdOverride = null;
        _model.AutoReconnectOverride = null;
        _model.DisplayNameOverride = null;
        _displayNameText = string.Empty;
        Raise(nameof(DisplayNameText), nameof(DisplayName), nameof(CredentialSelection), nameof(AutoReconnectChoice));

        if (_useOwnDisplay) UseOwnDisplay = false;
        else Touch();
    }

    public void Dispose() => Display.Dispose();

    // ------------------------------------------------------------------ internals

    private void Touch()
    {
        Raise(nameof(Summary), nameof(HasOverrides));
        _changed?.Invoke();
    }

    private static string MonitorNumbers(IReadOnlyList<MonitorInfo> monitors)
    {
        var numbers = new string[monitors.Count];
        for (var i = 0; i < numbers.Length; i++)
            numbers[i] = (monitors[i].Index + 1).ToString(CultureInfo.InvariantCulture);
        return string.Join(", ", numbers);
    }
}
