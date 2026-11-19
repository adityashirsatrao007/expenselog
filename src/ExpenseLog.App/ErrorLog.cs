using System.Text;

namespace ExpenseLog.App;

/// <summary>
/// Appends failures to %AppData%\ExpenseLog\crash.log so a report from a user contains
/// evidence. Silently gives up if the log itself cannot be written.
/// </summary>
public static class ErrorLog
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExpenseLog");

    public static string LogPath => Path.Combine(Folder, "crash.log");

    public static void Write(Exception exception, string context)
    {
        try
        {
            Directory.CreateDirectory(Folder);

            var entry = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {context}")
                .AppendLine($"{exception.GetType().FullName}: {exception.Message}")
                .AppendLine(exception.StackTrace)
                .AppendLine();

            File.AppendAllText(LogPath, entry.ToString());
        }
        catch (Exception)
        {
            // Never let logging kill the app.
        }
    }
}
