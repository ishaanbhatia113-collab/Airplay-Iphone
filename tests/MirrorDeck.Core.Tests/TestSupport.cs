using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MirrorDeck.Core;

namespace MirrorDeck.Core.Tests;

/// <summary>Runs only where a POSIX shell exists (the stand-in engine is a shell script).</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "needs a POSIX shell";
    }
}

/// <summary>
/// Runs only when MIRRORDECK_TEST_ENGINE points at a UxPlay executable (unmodified upstream build).
/// These tests bind the real AirPlay ports and send real mDNS queries.
/// </summary>
public sealed class EngineFactAttribute : FactAttribute
{
    public const string Variable = "MIRRORDECK_TEST_ENGINE";

    /// <param name="bufferedOutputOnly">
    /// The test is about an engine whose console output is block-buffered, which is how UxPlay behaves
    /// everywhere except on Windows.
    /// </param>
    public EngineFactAttribute(bool bufferedOutputOnly = false)
    {
        var path = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Skip = $"set {Variable} to a UxPlay executable to run this test";
        }
        else if (bufferedOutputOnly && OperatingSystem.IsWindows())
        {
            Skip = "UxPlay does not buffer its output on Windows";
        }
    }
}

/// <summary>Collects everything a host reports and lets a test wait for a condition.</summary>
internal sealed class HostRecorder
{
    private readonly Lock _gate = new();
    private readonly List<ReceiverStatus> _statuses = [];
    private readonly List<string> _log = [];
    private readonly EngineHost _host;

    public HostRecorder(EngineHost host)
    {
        _host = host;
        host.StatusChanged += s =>
        {
            lock (_gate) _statuses.Add(s);
        };
        host.LogLine += l =>
        {
            lock (_gate) _log.Add(l);
        };
    }

    public IReadOnlyList<ReceiverStatus> Statuses
    {
        get
        {
            lock (_gate) return _statuses.ToList();
        }
    }

    public IReadOnlyList<ReceiverState> States => Statuses.Select(s => s.State).ToList();

    public string Log
    {
        get
        {
            lock (_gate) return string.Join('\n', _log);
        }
    }

    public async Task<ReceiverStatus> WaitForAsync(Func<ReceiverStatus, bool> condition, double seconds = 30)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            var status = _host.Status;
            if (condition(status)) return status;
            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Condition not met within {seconds}s. Status: {_host.Status}. States seen: {string.Join(" > ", States)}.\nLog:\n{Log}");
    }

    public Task<ReceiverStatus> WaitForStateAsync(ReceiverState state, double seconds = 30) =>
        WaitForAsync(s => s.State == state, seconds);
}

internal static class Unix
{
    /// <summary>What Ctrl+C is on Windows: a polite request that lets the engine say goodbye on the network.</summary>
    public static bool Interrupt(Process process)
    {
        using var kill = Process.Start(new ProcessStartInfo("kill") { ArgumentList = { "-INT", process.Id.ToString() } });
        kill!.WaitForExit();
        return kill.ExitCode == 0;
    }
}

internal sealed record MdnsRecord(string Name, string Type, string Value);

/// <summary>
/// A minimal mDNS browser: asks the local network who offers a service, exactly as an iPhone does
/// when the AirPlay list is opened.
/// </summary>
internal static class MdnsBrowser
{
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");

    public static async Task<IReadOnlyList<MdnsRecord>> QueryAsync(TimeSpan duration, params string[] services)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, 5353));
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group));
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

        var query = new List<byte> { 0, 0, 0, 0, 0, (byte)services.Length, 0, 0, 0, 0, 0, 0 };
        foreach (var service in services)
        {
            foreach (var label in service.Split('.'))
            {
                var raw = Encoding.UTF8.GetBytes(label);
                query.Add((byte)raw.Length);
                query.AddRange(raw);
            }

            query.AddRange([0, 0, 12, 0, 1]); // root label, QTYPE=PTR, QCLASS=IN
        }

        var records = new HashSet<MdnsRecord>();
        var buffer = new byte[9000];
        var watch = Stopwatch.StartNew();
        var sent = 0;
        while (watch.Elapsed < duration)
        {
            if (sent < 3 && watch.Elapsed >= TimeSpan.FromMilliseconds(300 * sent))
            {
                await socket.SendToAsync(query.ToArray(), SocketFlags.None, new IPEndPoint(Group, 5353));
                sent++;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            try
            {
                var received = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
                foreach (var record in Parse(buffer.AsSpan(0, received))) records.Add(record);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
            {
                // somebody else's malformed packet
            }
        }

        return records.ToList();
    }

    private static List<MdnsRecord> Parse(ReadOnlySpan<byte> data)
    {
        var result = new List<MdnsRecord>();
        int questions = (data[4] << 8) | data[5];
        int total = ((data[6] << 8) | data[7]) + ((data[8] << 8) | data[9]) + ((data[10] << 8) | data[11]);
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            ReadName(data, ref offset);
            offset += 4;
        }

        for (var i = 0; i < total; i++)
        {
            var name = ReadName(data, ref offset);
            int type = (data[offset] << 8) | data[offset + 1];
            int ttl = (data[offset + 4] << 24) | (data[offset + 5] << 16) | (data[offset + 6] << 8) | data[offset + 7];
            int length = (data[offset + 8] << 8) | data[offset + 9];
            offset += 10;
            var rdata = data.Slice(offset, length);
            var at = offset;
            offset += length;
            if (ttl == 0) continue; // a goodbye

            switch (type)
            {
                case 12:
                    result.Add(new MdnsRecord(name, "PTR", ReadName(data, ref at)));
                    break;
                case 33:
                    var port = (rdata[4] << 8) | rdata[5];
                    at += 6;
                    result.Add(new MdnsRecord(name, "SRV", $"{ReadName(data, ref at)}:{port}"));
                    break;
                case 16:
                    var items = new List<string>();
                    for (var p = 0; p < rdata.Length;)
                    {
                        int l = rdata[p];
                        items.Add(Encoding.UTF8.GetString(rdata.Slice(p + 1, l)));
                        p += 1 + l;
                    }

                    result.Add(new MdnsRecord(name, "TXT", string.Join(' ', items)));
                    break;
                case 1:
                    result.Add(new MdnsRecord(name, "A", new IPAddress(rdata).ToString()));
                    break;
            }
        }

        return result;
    }

    private static string ReadName(ReadOnlySpan<byte> data, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        for (var hops = 0; hops < 64; hops++)
        {
            int length = data[position];
            if (length == 0)
            {
                position++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                var pointer = ((length & 0x3F) << 8) | data[position + 1];
                if (!jumped) offset = position + 2;
                jumped = true;
                position = pointer;
                continue;
            }

            labels.Add(Encoding.UTF8.GetString(data.Slice(position + 1, length)));
            position += 1 + length;
        }

        if (!jumped) offset = position;
        return string.Join('.', labels);
    }
}
