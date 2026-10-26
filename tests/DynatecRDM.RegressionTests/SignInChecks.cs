using System.Reflection;
using DynatecRDM.Data;
using DynatecRDM.Models;
using DynatecRDM.Rdp;
using DynatecRDM.Services;
using MSTSCLib;
using Microsoft.Data.Sqlite;

// Signing in as the Windows user, logins typed into the editor, and leaving a session on the
// remote console. Synthetic data only; nothing here authenticates or touches the user's database.
public static partial class Program
{
    private static void WindowsSignIn()
    {
        using var window = new RdpSessionWindow("Windows sign-in test");
        window.ShowInTaskbar = false;
        window.Location = new System.Drawing.Point(-20000, -20000);
        ShowWithoutActivation(window);
        var ax = window.Host.Control;
        var plan = new RdpDisplayPlan(1024, 768, 32, false, ResizeBehavior.FollowWindow, 100, 100);
        var me = CredentialSet.ForWindowsUser();
        Check(me.Username == Environment.UserName && !me.HasPassword, "The Windows user is not the signed-in user, or carries a password.");

        // Even a connection last set to prompt must not force the prompt: that stops single sign-on.
        var connection = new RdpConnection { Host = "test.invalid", UseWindowsSignIn = true, CredentialDelivery = CredentialDelivery.Prompt };
        RdpControlConfigurator.Configure(ax, connection, plan, new RdpCredential(me, null));
        var ns = (IMsRdpClientNonScriptable5)window.Host.Ocx;
        Check(!ns.PromptForCredentials, "Windows sign-in forced the credential prompt.");
        Check(ax.UserName == me.GetLogonName("test.invalid"), $"Windows sign-in sent '{ax.UserName}'.");

        var file = new RdpFileBuilder(new SecretProtector()).Build(connection, new RdpBuildContext { Credential = me });
        Check(file.Contains("prompt for credentials:i:0"), "The .rdp file forces the prompt for Windows sign-in.");
        Check(file.Contains($"username:s:{me.GetLogonName("test.invalid")}"), "The .rdp file does not name the Windows user.");
        Check(!file.Contains("password 51"), "The .rdp file carries a password for Windows sign-in.");

        // Policy entries, in the forms the "Allow delegating default credentials" policy documents.
        Check(CredentialDelegationPolicy.Matches("TERMSRV/*", "hmi01"), "TERMSRV/* did not match.");
        Check(CredentialDelegationPolicy.Matches("termsrv/HMI01", "hmi01"), "An exact entry is case-sensitive.");
        Check(CredentialDelegationPolicy.Matches("TERMSRV/*.plant.local", "hmi01.plant.local"), "A domain wildcard did not match.");
        Check(!CredentialDelegationPolicy.Matches("TERMSRV/*.plant.local", "hmi01.other.local"), "A domain wildcard matched another domain.");
        Check(!CredentialDelegationPolicy.Matches("HTTP/hmi01", "hmi01"), "Another service's entry matched Remote Desktop.");
        Check(CredentialDelegationPolicy.StatusFor("10.0.0.5:3390") == WindowsSignInStatus.NeedsComputerName, "An address was treated as a name.");
    }

    private static void ConsoleHandOffKeys()
    {
        var keys = new List<RdpKeyEvent>();
        Check(RdpKeyboard.TryType(RdpSessionWindow.ConsoleHandOffCommand, RdpKeyboard.CurrentLayout(), keys, out var missing),
            $"The current keyboard layout cannot type '{missing}'.");

        // Every key that goes down comes up again, so no modifier is left held in the session.
        var held = new Dictionary<(int, bool), int>();
        foreach (var key in keys)
        {
            held[(key.ScanCode, key.Extended)] = held.GetValueOrDefault((key.ScanCode, key.Extended)) + (key.Up ? -1 : 1);
            Check((key.LParam & 0xFFFF) == 1, "A key does not carry a repeat count of one.");
            Check(((uint)key.LParam & 0xC0000000) == (key.Up ? 0xC0000000 : 0), "A key's transition bits do not match its direction.");
        }
        Check(held.Values.All(v => v == 0), "The command leaves a key held down.");

        // On the Norwegian layout '$' is AltGr+4: the right Alt key, extended.
        var layout = RdpKeyboard.CurrentLayout();
        if (((long)layout & 0xFFFF) == 0x0414)
        {
            var dollar = new List<RdpKeyEvent>();
            Check(RdpKeyboard.TryType("$", layout, dollar, out _), "'$' cannot be typed.");
            Check(dollar.Count == 4 && dollar[0] == new RdpKeyEvent(0x38, true, false) && dollar[1].ScanCode == 0x05,
                $"'$' is not AltGr+4: {string.Join(", ", dollar)}");
        }

        var windowsR = RdpKeyboard.WindowsR();
        Check(windowsR[0] == new RdpKeyEvent(0x5B, true, false) && windowsR[^1] == new RdpKeyEvent(0x5B, true, true),
            "Windows+R does not wrap R in the Windows key.");
    }

    private static void ConnectionSignInStorage()
    {
        // Only synthetic connections in uniquely named databases beside this test executable.
        var fresh = System.IO.Path.Combine(AppContext.BaseDirectory, $"regression-{Guid.NewGuid():N}.db");
        var upgraded = System.IO.Path.Combine(AppContext.BaseDirectory, $"regression-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new SqliteDataStore(fresh))
            {
                store.InitializeAsync().GetAwaiter().GetResult();
                var connection = new RdpConnection { Name = "Test", Host = "test.invalid", UseWindowsSignIn = true, LeaveOnConsole = true };
                store.UpsertConnectionAsync(connection).GetAwaiter().GetResult();
                var back = store.GetConnectionAsync(connection.Id).GetAwaiter().GetResult()!;
                Check(back.UseWindowsSignIn && back.LeaveOnConsole, "The new connection settings did not round-trip.");
                Check(back.Clone() is { UseWindowsSignIn: true, LeaveOnConsole: true }, "Clone dropped the new settings.");
            }

            // A version 2 database, as every released build left it, with one connection in it.
            var id = Guid.NewGuid();
            using (var cn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = upgraded, Pooling = false }.ToString()))
            {
                cn.Open();
                using var cmd = cn.CreateCommand();
                cmd.CommandText = (string)typeof(SqliteDataStore).GetField("SchemaV1Sql", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!
                    + (string)typeof(SqliteDataStore).GetField("SchemaV2Sql", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!
                    + "PRAGMA user_version = 2;"
                    + $"INSERT INTO connections (id, name, host, created_utc, modified_utc) VALUES ('{id}', 'Old', 'old.invalid', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');";
                cmd.ExecuteNonQuery();
            }
            using (var store = new SqliteDataStore(upgraded))
            {
                store.InitializeAsync().GetAwaiter().GetResult();
                var old = store.GetConnectionAsync(id).GetAwaiter().GetResult();
                Check(old is { Name: "Old", UseWindowsSignIn: false, LeaveOnConsole: false }, "An upgraded connection was lost or changed.");
                old!.LeaveOnConsole = true;
                store.UpsertConnectionAsync(old).GetAwaiter().GetResult();
                Check(store.GetConnectionAsync(id).GetAwaiter().GetResult()!.LeaveOnConsole, "An upgraded database does not keep the new setting.");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { fresh, upgraded })
                foreach (var suffix in new[] { "", "-wal", "-shm" }) System.IO.File.Delete(path + suffix);
        }
    }
}
