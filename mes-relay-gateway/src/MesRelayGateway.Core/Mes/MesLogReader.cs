namespace MesRelayGateway.Mes;

/// <summary>
/// MES_HAI.dll logs through log4net to a file at the relative path "Log\MES_HAI.log",
/// resolved against the current working directory of whichever process loaded the DLL
/// (confirmed empirically: the file appears next to the exe that hosted the DLL, both for
/// MesHaiBridge.exe and for direct in-process loading). This reads only the bytes appended
/// since a previous checkpoint, so each MES call's captured log stays scoped to that call.
/// </summary>
public static class MesLogReader
{
    public static long GetLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Reads everything appended to the file after <paramref name="offset"/> bytes.</summary>
    public static string? ReadFrom(string path, long offset)
    {
        try
        {
            if (!File.Exists(path)) return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (offset < 0 || offset > stream.Length)
            {
                offset = 0; // file was rotated/truncated since the checkpoint
            }

            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }
}
