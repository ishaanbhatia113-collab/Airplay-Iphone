// A stand-in for uxplay.exe.
//
// It prints, with pauses, what the real engine prints while a device asks for a code, connects, mirrors
// and leaves again, and it opens a window where the real engine would show the picture. That is enough
// to walk the MirrorDeck UI through every state without an iPhone:
//
//     dotnet build tools/FakeEngine
//     (in MirrorDeck)  Advanced > Engine location > tools\FakeEngine\bin\Debug\net10.0-windows\uxplay.exe
//
// The lines are copied from the UxPlay 1.74 sources; MirrorDeck.Core/EngineLogParser.cs lists where each
// one comes from. Nothing here speaks AirPlay or opens a network port.

using System.Text;
using System.Windows.Forms;

namespace MirrorDeck.FakeEngine;

internal static class Program
{
    private static readonly CancellationTokenSource Stop = new();
    private static readonly Lock OutputGate = new();

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);

        // What UxPlay does on Ctrl+C: leave the main loop and say goodbye.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Stop.Cancel();
        };

        var options = ReadOptions(args);
        if (options is null) return 0; // UxPlay exits with 0 here as well

        var name = options.FirstOrDefault(o => o.StartsWith("n ", StringComparison.Ordinal))?[2..].Trim('"') ?? "UxPlay";
        var asksForCode = options.Contains("pin") || options.Contains("pw");

        Say("UxPlay 1.74: An Open-Source AirPlay mirroring and audio-streaming server.");
        Say("public key storage (for persistence) is in receiver-key.pem");
        Say("using network ports UDP 7011 6001 6000 TCP 7100 7000 7001");
        Say("using system MAC address 02:00:00:fa:4e:01");
        Say($"FAKE ENGINE: pretending to be \"{name}\". No device can really connect to this.");
        Say("Initialized server socket(s)");

        try
        {
            for (var session = 1; ; session++)
            {
                Pause(5);
                RunSession(session, asksForCode);
            }
        }
        catch (OperationCanceledException)
        {
            Say("Stopping RAOP Server...");
            return 0;
        }
    }

    private static void RunSession(int session, bool asksForCode)
    {
        var socket = 30 + session;
        var device = session % 2 == 1 ? ("Ishaan’s iPhone", "iPhone15,3") : ("Living Room iPad", "iPad13,4");

        Say($"Accepted IPv4 client on socket {socket}, port 7000");
        if (asksForCode)
        {
            Say("client sent PAIR-PIN-START request");
            Say($"*** CLIENT MUST NOW ENTER PIN = \"{1000 + (session * 2917) % 9000:0000}\" AS AIRPLAY PASSWORD");
            Pause(6);
        }

        Say($"connection request from {device.Item1} ({device.Item2}) with deviceID = A4:83:E7:11:22:{session % 100:00}");
        Say(string.Empty);
        Pause(2);

        Say("raop_rtp_mirror starting mirroring");
        Say("raop_rtp starting audio");
        Say("ct=8 spf=480 usingScreen=1 isMedia=0  audioFormat=0x1000000");
        Say("Begin streaming to GStreamer video pipeline");

        ShowMirrorWindow(TimeSpan.FromSeconds(20));

        Say("raop_rtp_mirror->running is no longer true");
        Say($"Connection closed on socket {socket}");
    }

    /// <summary>The real engine's window is created by GStreamer and is called "Direct3D11 renderer".</summary>
    private static void ShowMirrorWindow(TimeSpan duration)
    {
        var thread = new Thread(() =>
        {
            using var form = new Form
            {
                Text = "Direct3D11 renderer",
                BackColor = System.Drawing.Color.Black,
                ClientSize = new System.Drawing.Size(480, 270),
                KeyPreview = true,
            };
            form.Controls.Add(new Label
            {
                Text = "FAKE ENGINE\nthe mirrored screen would be here",
                ForeColor = System.Drawing.Color.Gray,
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
            });

            var fullscreen = false;
            form.KeyDown += (_, e) =>
            {
                if (!(e.Alt && e.KeyCode == Keys.Enter)) return;
                fullscreen = !fullscreen;
                form.FormBorderStyle = fullscreen ? FormBorderStyle.None : FormBorderStyle.Sizable;
                form.WindowState = fullscreen ? FormWindowState.Maximized : FormWindowState.Normal;
                Say($"FAKE ENGINE: Alt+Enter received, full screen is now {(fullscreen ? "on" : "off")}");
                e.Handled = true;
            };

            var timer = new System.Windows.Forms.Timer { Interval = 200 };
            var shown = DateTime.UtcNow;
            string? lastTitle = null;
            timer.Tick += (_, _) =>
            {
                if (form.Text != lastTitle)
                {
                    lastTitle = form.Text;
                    Say($"FAKE ENGINE: window title is \"{lastTitle}\"");
                }

                if (Stop.IsCancellationRequested || DateTime.UtcNow - shown > duration) form.Close();
            };
            timer.Start();
            Application.Run(form);
            timer.Dispose();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Stop.Token.ThrowIfCancellationRequested();
    }

    private static List<string>? ReadOptions(string[] args)
    {
        var index = Array.IndexOf(args, "-rc");
        if (index < 0 || index + 1 >= args.Length) return [];

        var file = args[index + 1];
        if (!File.Exists(file))
        {
            Say($"*** ERROR: startup file {file} specified by option -rc was not found");
            return null;
        }

        Say($"UxPlay: reading configuration from  {file}");
        return File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#').ToList();
    }

    private static void Pause(int seconds)
    {
        if (Stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds))) throw new OperationCanceledException();
    }

    private static void Say(string line)
    {
        lock (OutputGate)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }
}
