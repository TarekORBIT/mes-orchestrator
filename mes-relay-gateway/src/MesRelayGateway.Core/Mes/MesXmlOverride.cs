namespace MesRelayGateway.Mes;

/// <summary>
/// MES_HAI.dll does not use the haiXmlPath we pass around this tool - it always resolves its
/// server list itself from a fixed local path (confirmed via its own log:
/// "Traceability() RETURN Configuration file found on C:\ProgramData\MESApps\CIM\MES_HAI.xml").
/// That constructor also unconditionally runs LoadBalancing() against whatever servers that
/// file lists, before Login() is ever called - there is no way to load the DLL "quietly".
///
/// For Mode Test DLL (explicitly meant to work without the Visteon network) this class swaps
/// that file's content for a local, instantly-refusing address for the duration of one call,
/// then restores the original - so the DLL still does real work (real load, real log4net/
/// LogLibrary output) but fails in milliseconds instead of ~1-2 minutes of real TCP timeouts,
/// and never actually reaches the real CIM servers.
///
/// Safety: the backup is written to disk (not just kept in memory) before the file is
/// overwritten, and every entry point restores from it first if a previous run left one behind
/// (e.g. the process was killed mid-call) - so a crash can never permanently leave the fake
/// addresses in place.
/// </summary>
public static class MesXmlOverride
{
    /// <summary>The fixed path MES_HAI.dll itself reads - not configurable, see class remarks.</summary>
    public const string FixedXmlPath = @"C:\ProgramData\MESApps\CIM\MES_HAI.xml";

    private const string FakeServersXml = """
        <configuration>
          <Servers>
            <Server IpAddress="127.0.0.1" Port="1" Description="MesRelayGateway Mode Test DLL - fake address, not a real MES server"/>
            <Server IpAddress="127.0.0.1" Port="2" Description="MesRelayGateway Mode Test DLL - fake address, not a real MES server"/>
          </Servers>
        </configuration>
        """;

    private static string BackupPath(string xmlPath) => xmlPath + ".mes-relay-gateway.bak";

    /// <summary>
    /// Swaps <paramref name="xmlPath"/> for a fast-failing local stand-in until the returned
    /// object is disposed. Returns null (no swap performed) if the file doesn't exist, so
    /// callers should fall back to normal behavior in that case.
    /// </summary>
    public static IDisposable? Apply(string xmlPath)
    {
        RestoreIfNeeded(xmlPath);
        if (!File.Exists(xmlPath)) return null;

        return new Scope(xmlPath);
    }

    /// <summary>
    /// Restores <paramref name="xmlPath"/> from a leftover backup, if one exists (meaning a
    /// previous Mode Test DLL run didn't get to clean up after itself). Safe and cheap to call
    /// before any Mode Reel run, as a defensive guard against real calls ever seeing fake IPs.
    /// </summary>
    public static void RestoreIfNeeded(string xmlPath)
    {
        var backupPath = BackupPath(xmlPath);
        if (!File.Exists(backupPath)) return;

        File.Copy(backupPath, xmlPath, overwrite: true);
        File.Delete(backupPath);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _xmlPath;
        private readonly string _backupPath;
        private bool _disposed;

        public Scope(string xmlPath)
        {
            _xmlPath = xmlPath;
            _backupPath = BackupPath(xmlPath);

            File.Copy(_xmlPath, _backupPath, overwrite: true);
            File.WriteAllText(_xmlPath, FakeServersXml);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (File.Exists(_backupPath))
                {
                    File.Copy(_backupPath, _xmlPath, overwrite: true);
                    File.Delete(_backupPath);
                }
            }
            catch
            {
                // Best effort: if this fails, the backup file survives on disk and the next
                // Apply()/RestoreIfNeeded() call (from either mode) will pick it up.
            }
        }
    }
}
