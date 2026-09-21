namespace DynatecRDM.Services;

/// <summary>
/// What the app is called and who publishes it. The app is "Remote Desktop Manager"; DYNATEC is
/// the publisher, not part of the name, so it appears only where a publisher is meant.
/// Identifiers that only look like names - the DynatecRDM data folder, executable, registry keys
/// and file names - stay as they are: renaming them would strand existing installs and data.
/// </summary>
public static class AppIdentity
{
    public const string Name = "Remote Desktop Manager";

    public const string Publisher = "DYNATEC";
}
