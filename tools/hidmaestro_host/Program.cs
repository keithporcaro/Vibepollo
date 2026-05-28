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

        // ControllerManager owns the HMContext and dispatches state mutations.
        // Pipe sends are serialized through a single SemaphoreSlim so callbacks
        // from HIDMaestro's polling thread don't interleave with main-loop sends.
        var writeLock = new SemaphoreSlim(1, 1);
        async Task SendFrameSerialized(byte[] payload)
        {
            await writeLock.WaitAsync(ct);
            try
            {
                await SendFrameAsync(server, payload, ct);
            }
            finally
            {
                writeLock.Release();
            }
        }

        using var manager = new ControllerManager(SendFrameSerialized);

        // Send our HELLO unprompted; the host will send theirs in response.
        await SendFrameSerialized(WireEncode.EncodeHello(new HelloFrame(
            ProtocolVersion: Protocol.Version,
            SupportedOpcodeMask: SupportedOpcodeMask(),
            SupportedProfiles: manager.SupportedProfiles)));

        try
        {
            while (!ct.IsCancellationRequested && server.IsConnected)
            {
                var frame = await ReadFrameAsync(server, ct);
                if (frame is null) break;
                Dispatch(frame, manager);
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
        // Roundtrip a HELLO through the codec, then ALLOC + FREE one controller
        // through the manager. With HIDMaestro.Core linked this exercises the
        // driver path; without it the manager logs a no-op and we still verify
        // the dispatch table.
        var hello = WireEncode.EncodeHello(new HelloFrame(
            ProtocolVersion: Protocol.Version,
            SupportedOpcodeMask: SupportedOpcodeMask(),
            SupportedProfiles: Array.Empty<string>()));
        var (op, ver) = WireDecode.PeekHeader(hello);
        if (op != Opcode.Hello || ver != Protocol.Version)
        {
            Console.Error.WriteLine($"[hidmaestro-host] selftest failed: roundtrip mismatch ({op} v{ver})");
            return Task.FromResult(1);
        }

        using var manager = new ControllerManager(_ => Task.CompletedTask);
        manager.Alloc(new AllocFrame(
            PadIndex: 0,
            ClientRelativeIndex: 0,
            Profile: "xbox-360-wired",
            ClientType: LiCType.Xbox,
            Capabilities: 0,
            SupportedButtons: 0));
        manager.Free(0);

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

    private static void Dispatch(byte[] frame, ControllerManager manager)
    {
        var (op, _) = WireDecode.PeekHeader(frame);
        switch (op)
        {
            case Opcode.Hello:
                var h = WireDecode.DecodeHello(frame);
                Console.Error.WriteLine($"[hidmaestro-host] host hello v{h.ProtocolVersion} mask=0x{h.SupportedOpcodeMask:X}");
                break;
            case Opcode.Alloc:
                manager.Alloc(WireDecode.DecodeAlloc(frame));
                break;
            case Opcode.Free:
                manager.Free(WireDecode.DecodeFree(frame));
                break;
            case Opcode.State:
                manager.State(WireDecode.DecodeState(frame));
                break;
            case Opcode.Touch:
                manager.Touch(WireDecode.DecodeTouch(frame));
                break;
            case Opcode.Motion:
                manager.Motion(WireDecode.DecodeMotion(frame));
                break;
            case Opcode.Battery:
                manager.Battery(WireDecode.DecodeBattery(frame));
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
