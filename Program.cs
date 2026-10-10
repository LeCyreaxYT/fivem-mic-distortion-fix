using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.Json;

namespace ApolloMicBridge;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Length == 2 && args[0] == "--devices")
            {
                File.WriteAllText(args[1], JsonSerializer.Serialize(DeviceCatalog.Read(), JsonOptions), Encoding.UTF8);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--smoke-test")
            {
                using var form = new MainForm();
                using var settle = new System.Windows.Forms.Timer { Interval = 500 };
                var result = 0;
                form.Shown += (_, _) => form.PostToUi(() =>
                {
                    try { form.VerifyShell(); settle.Start(); }
                    catch (Exception error)
                    {
                        AppLog.Write("Native window smoke check failed", error);
                        result = 1;
                        form.RequestExit();
                    }
                });
                settle.Tick += (_, _) =>
                {
                    settle.Stop();
                    try
                    {
                        if (form.WindowState != FormWindowState.Normal || form.ClientSize.Width < 700 || form.ClientSize.Height < 600)
                            throw new InvalidOperationException($"Restored window has no usable client area: {form.WindowState}, {form.Bounds}, {form.RestoreBounds}, {form.ClientSize}.");
                        using var bitmap = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    }
                    catch (Exception error) { AppLog.Write("Native window smoke check failed", error); result = 1; }
                    form.RequestExit();
                };
                Application.Run(form);
                return result;
            }
            if (args.Length == 2 && args[0] == "--smoke-idle")
            {
                using var form = new MainForm();
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                using var timer = new System.Windows.Forms.Timer { Interval = 5000 };
                var measuring = false;
                var cpuStart = TimeSpan.Zero;
                var elapsed = new System.Diagnostics.Stopwatch();
                var refreshStart = 0;
                var requestStart = 0;
                form.Shown += (_, _) => form.PostToUi(form.Hide);
                timer.Tick += (_, _) =>
                {
                    if (!measuring)
                    {
                        cpuStart = process.TotalProcessorTime;
                        refreshStart = form.DeviceRefreshCount;
                        requestStart = form.DeviceUpdateRequests;
                        elapsed.Start();
                        timer.Interval = 20000;
                        measuring = true;
                        return;
                    }
                    elapsed.Stop();
                    process.Refresh();
                    File.WriteAllText(args[1], JsonSerializer.Serialize(new
                    {
                        CpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds,
                        DurationMs = elapsed.Elapsed.TotalMilliseconds,
                        DeviceRefreshes = form.DeviceRefreshCount - refreshStart,
                        DeviceUpdateRequests = form.DeviceUpdateRequests - requestStart,
                        WindowVisible = form.Visible
                    }, JsonOptions));
                    timer.Stop();
                    form.RequestExit();
                };
                timer.Start();
                Application.Run(form);
                return 0;
            }
            if (args.SequenceEqual(new[] { "--quit" }))
                return SingleInstance.RequestShutdown() ? 0 : 2;
            if (args.Any(arg => arg is not ("--background" or "--autostart")))
                throw new ArgumentException("Unknown application argument.");
            using var instance = new SingleInstance();
            if (!instance.IsFirst)
            {
                instance.HandleAnotherLaunch(args.Contains("--background"));
                return 0;
            }
            using var main = new MainForm(args.Contains("--autostart"), args.Contains("--background"));
            instance.Listen(main);
            Application.Run(main);
            return 0;
        }
        catch (Exception error)
        {
            AppLog.Write("Application failed", error);
            if (args.Length != 0)
                return 1;
            MessageBox.Show(Texts.Get("AppError") + "\n\n" + error.Message, Texts.Get("Title"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}

internal static class Texts
{
    private static readonly ResourceManager Resources = new("ApolloMicBridge.Strings", typeof(Texts).Assembly);

    public static string Get(string key, params object[] values)
    {
        var text = Resources.GetString(key, CultureInfo.CurrentUICulture)
            ?? throw new InvalidOperationException("Missing localized text: " + key);
        return values.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, values);
    }
}

internal static class AppLog
{
    internal static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkySystems", "ApolloMicBridge");

    public static void Write(string message, Exception? error = null)
    {
        System.Diagnostics.Trace.WriteLine(message + " " + error);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, "app.log");
            lock (typeof(AppLog))
            {
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                    File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}\n{error}\n", Encoding.UTF8);
            }
        }
        catch (IOException logError)
        {
            System.Diagnostics.Trace.WriteLine("Cannot write diagnostic log: " + logError);
        }
        catch (UnauthorizedAccessException logError)
        {
            System.Diagnostics.Trace.WriteLine("Diagnostic log access denied: " + logError);
        }
    }
}
