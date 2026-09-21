namespace DynatecRDM.Services;

/// <summary>
/// The window-level operations the tray icon and view models need from the application object.
/// Implemented by App so nothing below it has to reach for Application.Current.
/// </summary>
public interface IAppShell
{
    /// <summary>Shows and focuses the main window, creating it on first use.</summary>
    void ShowMain();

    /// <summary>Shows the main window and selects a connection or multi-config in the tree.</summary>
    void ShowMain(Guid selectId);

    /// <summary>Opens the credential manager dialog.</summary>
    void ShowCredentials();

    /// <summary>Opens the settings dialog.</summary>
    void ShowSettings();

    /// <summary>Opens the update dialog and starts a check.</summary>
    void ShowUpdates();

    /// <summary>Opens the export/import dialog, optionally on the import tab.</summary>
    void ShowTransfer(bool startOnImportTab = false);

    /// <summary>Opens the quick-launch menu at the cursor (the tray popup).</summary>
    void ShowQuickLaunch();

    /// <summary>Hides the main window to the notification area.</summary>
    void HideMain();

    /// <summary>Shuts the application down.</summary>
    void ExitApplication();

    /// <summary>Shows a transient notification from the tray icon.</summary>
    void Notify(string title, string message, bool isError = false);

    /// <summary>
    /// Tells the user about something they have to act on - too long, or too important, for a
    /// passing notification. Shells without a dialog of their own fall back to a notification.
    /// </summary>
    void ShowNotice(string title, string message) => Notify(title, message, true);
}
