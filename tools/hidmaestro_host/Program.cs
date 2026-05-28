// Entry point for the Vibepollo HIDMaestro sidecar.
//
// Lifecycle:
//   1. Vibepollo (host) spawns this process via ProcessHandler with --pipe <name>.
//   2. We open a duplex named pipe in server mode and wait for the host to connect.
//   3. Handshake: receive HELLO from host, reply with HELLO carrying our protocol
//      version, supported opcode mask, and the list of HIDMaestro profiles we can
//      deploy.
//   4. Main loop: dispatch each incoming opcode to the controller manager.
//   5. Forward HMController.OutputReceived events back to the host as RUMBLE / LED
//      / TRIGGER_EFFECT frames.
//
// This file is the IPC + dispatch skeleton. HIDMaestro.Core integration lives in
// ControllerManager.cs (to be added in a follow-up commit alongside per-profile
// mappers).

using System.CommandLine;
using System.IO.Pipes;

namespace VibePollo.HidMaestroHost;

internal static class Program
{
    internal const int FramePrefixBytes = 4;
    internal const int MaxFrameBytes = 2 * 1024 * 1024;

    public static async Task<int> Main(string[] args)
    {
        var pipeOption = new Option<string>("--pipe")
        {
            Description = "Name of the duplex named pipe used to talk to the host.",
            Required = true,
        };
        var selftestOption = new Option<bool>("--selftest")
        {
            Description = "Run a minimal self-test (allocate and free one controller) and exit.",
        };

        var root = new RootCommand("Vibepollo HIDMaestro sidecar");
        root.Options.Add(pipeOption);
        root.Options.Add(selftestOption);

        root.SetAction(async (parseResult, ct) =>
        {
            var pipe = parseResult.GetValue(pipeOption)!;
            var selftest = parseResult.GetValue(selftestOption);

            if (selftest)
            {
                return await RunSelfTestAsync(ct);
            }

            return await RunIpcServerAsync(pipe, ct);
        });

        return await root.Parse(args).InvokeAsync();
    }

    private static async Task<int> RunIpcServerAsync(string pipeName, CancellationToken ct)
    {
        Console.Error.WriteLine($"[hidmaestro-host] starting on pipe '{pipeName}'");

        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        try
        {
            await server.WaitForConnectionAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }

        Console.Error.WriteLine("[hidmaestro-host] host connected");

        // Send our HELLO unprompted; the host will send theirs in response.
        await SendFrameAsync(server, WireEncode.EncodeHello(new HelloFrame(
            ProtocolVersion: Protocol.Version,
            SupportedOpcodeMask: SupportedOpcodeMask(),
            SupportedProfiles: SupportedProfiles())), ct);

        try
        {
            while (!ct.IsCancellationRequested && server.IsConnected)
            {
                var frame = await ReadFrameAsync(server, ct);
                if (frame is null) break;
                Dispatch(frame);
            }
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[hidmaestro-host] pipe error: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static Task<int> RunSelfTestAsync(CancellationToken ct)
    {
        // Will allocate + free one HIDMaestro controller via HMContext once the
        // ControllerManager class lands. For now we just confirm the wire-format
        // code can encode/decode its own frames.
        var hello = WireEncode.EncodeHello(new HelloFrame(
            ProtocolVersion: Protocol.Version,
            SupportedOpcodeMask: SupportedOpcodeMask(),
            SupportedProfiles: SupportedProfiles()));
        var (op, ver) = WireDecode.PeekHeader(hello);
        if (op != Opcode.Hello || ver != Protocol.Version)
        {
            Console.Error.WriteLine($"[hidmaestro-host] selftest failed: roundtrip mismatch ({op} v{ver})");
            return Task.FromResult(1);
        }
        Console.WriteLine("[hidmaestro-host] selftest ok");
        return Task.FromResult(0);
    }

    private static ulong SupportedOpcodeMask()
    {
        return Protocol.OpcodeBit(Opcode.Hello)
             | Protocol.OpcodeBit(Opcode.Alloc)
             | Protocol.OpcodeBit(Opcode.Free)
             | Protocol.OpcodeBit(Opcode.State)
             | Protocol.OpcodeBit(Opcode.Touch)
             | Protocol.OpcodeBit(Opcode.Motion)
             | Protocol.OpcodeBit(Opcode.Battery)
             | Protocol.OpcodeBit(Opcode.Rumble)
             | Protocol.OpcodeBit(Opcode.Led)
             | Protocol.OpcodeBit(Opcode.TriggerEffect);
    }

    private static IReadOnlyList<string> SupportedProfiles()
    {
        // Populated from HMContext.AllProfiles once HIDMaestro.Core is wired in.
        return Array.Empty<string>();
    }

    private static void Dispatch(byte[] frame)
    {
        var (op, _) = WireDecode.PeekHeader(frame);
        switch (op)
        {
            case Opcode.Hello:
                var h = WireDecode.DecodeHello(frame);
                Console.Error.WriteLine($"[hidmaestro-host] host hello v{h.ProtocolVersion} mask=0x{h.SupportedOpcodeMask:X}");
                break;
            case Opcode.Alloc:
                var a = WireDecode.DecodeAlloc(frame);
                Console.Error.WriteLine($"[hidmaestro-host] alloc pad={a.PadIndex} profile={a.Profile}");
                break;
            case Opcode.Free:
                var idx = WireDecode.DecodeFree(frame);
                Console.Error.WriteLine($"[hidmaestro-host] free pad={idx}");
                break;
            case Opcode.State:
            case Opcode.Touch:
            case Opcode.Motion:
            case Opcode.Battery:
                // Wire-up to ControllerManager lands in a follow-up commit.
                break;
            default:
                Console.Error.WriteLine($"[hidmaestro-host] unknown opcode {op}");
                break;
        }
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream s, CancellationToken ct)
    {
        var hdr = new byte[FramePrefixBytes];
        var read = 0;
        while (read < hdr.Length)
        {
            var n = await s.ReadAsync(hdr.AsMemory(read, hdr.Length - read), ct);
            if (n == 0) return null;
            read += n;
        }
        var len = (uint)(hdr[0] | (hdr[1] << 8) | (hdr[2] << 16) | (hdr[3] << 24));
        if (len == 0 || len > MaxFrameBytes)
        {
            throw new InvalidDataException($"frame length out of range: {len}");
        }
        var body = new byte[len];
        read = 0;
        while (read < body.Length)
        {
            var n = await s.ReadAsync(body.AsMemory(read, body.Length - read), ct);
            if (n == 0) return null;
            read += n;
        }
        return body;
    }

    private static async Task SendFrameAsync(Stream s, byte[] payload, CancellationToken ct)
    {
        var hdr = new byte[FramePrefixBytes];
        var len = (uint)payload.Length;
        hdr[0] = (byte)(len & 0xFF);
        hdr[1] = (byte)((len >> 8) & 0xFF);
        hdr[2] = (byte)((len >> 16) & 0xFF);
        hdr[3] = (byte)((len >> 24) & 0xFF);
        await s.WriteAsync(hdr, ct);
        await s.WriteAsync(payload, ct);
        await s.FlushAsync(ct);
    }
}
