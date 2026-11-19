namespace ExpenseLog.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

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
