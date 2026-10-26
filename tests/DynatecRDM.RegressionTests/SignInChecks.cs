using System.Reflection;
using DynatecRDM.Data;
using DynatecRDM.Models;
using DynatecRDM.Rdp;
using DynatecRDM.Services;
using MSTSCLib;
using Microsoft.Data.Sqlite;

// Signing in as the Windows user and logins typed into the editor. Synthetic data only; nothing
// here authenticates or touches the user's database.
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

    private static void ConnectionSignInStorage()
    {
        // Only synthetic connections in uniquely named databases beside this test executable.
        var fresh = System.IO.Path.Combine(AppContext.BaseDirectory, $"regression-{Guid.NewGuid():N}.db");
        var upgraded = System.IO.Path.Combine(AppContext.BaseDirectory, $"regression-{Guid.NewGuid():N}.db");
        try
        {
            var connection = new RdpConnection { Name = "Test", Host = "test.invalid", UseWindowsSignIn = true };
            using (var store = new SqliteDataStore(fresh))
            {
                store.InitializeAsync().GetAwaiter().GetResult();
                store.UpsertConnectionAsync(connection).GetAwaiter().GetResult();
                var back = store.GetConnectionAsync(connection.Id).GetAwaiter().GetResult()!;
                Check(back.UseWindowsSignIn, "Windows sign-in did not round-trip.");
                Check(back.Clone() is { UseWindowsSignIn: true }, "Clone dropped Windows sign-in.");
            }

            // 1.0.12 and 1.0.13 also stored whether to leave a session on the remote console. The
            // column is no longer read or written; a connection that has it set still loads and saves.
            using (var cn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fresh, Pooling = false }.ToString()))
            {
                cn.Open();
                using var cmd = cn.CreateCommand();
                cmd.CommandText = $"UPDATE connections SET leave_on_console = 1 WHERE id = '{connection.Id}';";
                Check(cmd.ExecuteNonQuery() == 1, "The stored connection was not found to mark.");
            }
            using (var store = new SqliteDataStore(fresh))
            {
                store.InitializeAsync().GetAwaiter().GetResult();
                var back = store.GetConnectionAsync(connection.Id).GetAwaiter().GetResult()!;
                back.Name = "Renamed";
                store.UpsertConnectionAsync(back).GetAwaiter().GetResult();
                Check(store.GetConnectionAsync(connection.Id).GetAwaiter().GetResult()?.Name == "Renamed",
                    "A connection stored by 1.0.12 or 1.0.13 no longer loads and saves.");
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
                Check(old is { Name: "Old", UseWindowsSignIn: false }, "An upgraded connection was lost or changed.");
                old!.UseWindowsSignIn = true;
                store.UpsertConnectionAsync(old).GetAwaiter().GetResult();
                Check(store.GetConnectionAsync(id).GetAwaiter().GetResult()!.UseWindowsSignIn, "An upgraded database does not keep Windows sign-in.");
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
