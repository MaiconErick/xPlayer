using System.IO.Pipes;
using System.Windows;

namespace xPlayer;

public partial class App : Application
{
    const string PipeName = "xPlayer-open-files";
    Mutex? singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (Cli.TryRun(e.Args)) { Shutdown(); return; }

        var paths = e.Args.Where(a => !a.StartsWith("--") && (File.Exists(a) || Directory.Exists(a))).Select(Path.GetFullPath).ToArray();
        bool screenshot = e.Args.Contains("--screenshot");
        singleInstance = new Mutex(true, "xPlayer-single-instance", out bool first);
        if (!first && !screenshot)
        {
            SendToRunningInstance(paths);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) => { Log.Error("unhandled", ex.Exception); ex.Handled = true; };
        base.OnStartup(e);
        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
        if (!screenshot) StartPipeServer(window);
    }

    static void SendToRunningInstance(string[] paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2500);
            using var w = new StreamWriter(client) { AutoFlush = true };
            foreach (var p in paths) w.WriteLine(p);
        }
        catch (Exception e) { Log.Error("could not reach the running xPlayer", e); }
    }

    static void StartPipeServer(MainWindow window)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte);
                    server.WaitForConnection();
                    using var r = new StreamReader(server);
                    var list = new List<string>();
                    string? line;
                    while ((line = r.ReadLine()) != null) if (line.Length > 0) list.Add(line);
                    window.Dispatcher.BeginInvoke(() => window.OpenFromOtherInstance(list.ToArray()));
                }
                catch (Exception e) { Log.Error("pipe server", e); Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "xplayer-ipc" };
        t.Start();
    }
}
