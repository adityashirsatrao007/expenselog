namespace ExpenseLog.App;

internal static class Program
{
    private const string InstanceMutexName = @"Local\ExpenseLog.SingleInstance";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // One writer per database file. SQLite would allow a second copy to start and then
        // fail with "database is locked" under load, which is not something a user can diagnose.
        using var instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);

        if (!createdNew)
        {
            MessageBox.Show(
                "ExpenseLog is already running.\n\nTwo copies writing the same database would conflict.",
                "ExpenseLog",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.ThreadException += (_, args) => ReportFailure(args.Exception, "ui thread");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                ReportFailure(exception, "app domain");
        };

        Application.Run(new MainForm());
    }

    private static void ReportFailure(Exception exception, string context)
    {
        ErrorLog.Write(exception, context);

        MessageBox.Show(
            $"ExpenseLog hit an unexpected error.\n\n{exception.Message}\n\nDetails: {ErrorLog.LogPath}",
            "ExpenseLog",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
