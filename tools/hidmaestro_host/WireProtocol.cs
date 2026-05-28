// Wire-format codec mirroring src/platform/windows/ipc/hidmaestro_ipc.{h,cpp}.
//
// Frames sit inside FramedPipe's length-prefix on the wire:
//
//   [u8 opcode][u8 version][payload]
//
// Encoders write little-endian, no padding. Decoders mirror that exactly.
// Adding a new opcode here MUST be matched on the C++ side and vice versa.

using System.Buffers.Binary;
using System.Text;

namespace VibePollo.HidMaestroHost;

internal enum Opcode : byte
{
    // Host -> sidecar
    Hello = 0x00,
    Alloc = 0x01,
    Free = 0x02,
    State = 0x03,
    Touch = 0x04,
    Motion = 0x05,
    Battery = 0x06,

    // Reserved for Steam-Controller-class client extension.
    StateEx = 0x10,
    Trackpad = 0x11,

    // Sidecar -> host
    Rumble = 0x80,
    Led = 0x81,
    TriggerEffect = 0x82,

    // Reserved for SC linear-actuator haptics.
    Haptic = 0x90,
}

internal static class Protocol
{
    public const byte Version = 1;

    public static ulong OpcodeBit(Opcode op) => 1UL << (byte)op;
}

internal readonly record struct AllocFrame(
    ushort PadIndex,
    byte ClientRelativeIndex,
    string Profile,
    byte ClientType,
    ushort Capabilities,
    uint SupportedButtons);

internal readonly record struct StateFrame(
    ushort PadIndex,
    uint ButtonFlags,
    byte Lt,
    byte Rt,
    short LsX,
    short LsY,
    short RsX,
    short RsY);

internal readonly record struct TouchFrame(
    ushort PadIndex,
    byte EventType,
    uint PointerId,
    float X,
    float Y,
    float Pressure);

internal readonly record struct MotionFrame(
    ushort PadIndex,
    byte MotionType,
    float X,
    float Y,
    float Z);

internal readonly record struct BatteryFrame(
    ushort PadIndex,
    byte State,
    byte Percentage);

internal readonly record struct HelloFrame(
    byte ProtocolVersion,
    ulong SupportedOpcodeMask,
    IReadOnlyList<string> SupportedProfiles);

internal readonly record struct RumbleFrame(ushort PadIndex, ushort LowFreq, ushort HighFreq);
internal readonly record struct LedFrame(ushort PadIndex, byte R, byte G, byte B);

internal ref struct FrameReader
{
    private readonly ReadOnlySpan<byte> _bytes;
    private int _pos;

    public FrameReader(ReadOnlySpan<byte> bytes)
    {
        _bytes = bytes;
        _pos = 0;
    }

    public bool Need(int n) => _pos + n <= _bytes.Length;

    public byte ReadU8()
    {
        if (!Need(1)) throw new InvalidDataException("frame truncated (u8)");
        return _bytes[_pos++];
    }

    public ushort ReadU16()
    {
        if (!Need(2)) throw new InvalidDataException("frame truncated (u16)");
        var v = BinaryPrimitives.ReadUInt16LittleEndian(_bytes[_pos..]);
        _pos += 2;
        return v;
    }

    public uint ReadU32()
    {
        if (!Need(4)) throw new InvalidDataException("frame truncated (u32)");
        var v = BinaryPrimitives.ReadUInt32LittleEndian(_bytes[_pos..]);
        _pos += 4;
        return v;
    }

    public ulong ReadU64()
    {
        if (!Need(8)) throw new InvalidDataException("frame truncated (u64)");
        var v = BinaryPrimitives.ReadUInt64LittleEndian(_bytes[_pos..]);
        _pos += 8;
        return v;
    }

    public short ReadI16() => (short)ReadU16();

    public float ReadF32()
    {
        var bits = ReadU32();
        return BitConverter.Int32BitsToSingle((int)bits);
    }

    public string ReadString()
    {
        var len = ReadU16();
        if (!Need(len)) throw new InvalidDataException("frame truncated (string body)");
        var s = Encoding.UTF8.GetString(_bytes[_pos..(_pos + len)]);
        _pos += len;
        return s;
    }
}

internal sealed class FrameWriter
{
    private readonly List<byte> _buf = new();

    public byte[] ToArray() => _buf.ToArray();

    public void WriteHeader(Opcode op)
    {
        _buf.Add((byte)op);
        _buf.Add(Protocol.Version);
    }

    public void WriteU8(byte v) => _buf.Add(v);

    public void WriteU16(ushort v)
    {
        _buf.Add((byte)(v & 0xFF));
        _buf.Add((byte)((v >> 8) & 0xFF));
    }

    public void WriteU32(uint v)
    {
        _buf.Add((byte)(v & 0xFF));
        _buf.Add((byte)((v >> 8) & 0xFF));
        _buf.Add((byte)((v >> 16) & 0xFF));
        _buf.Add((byte)((v >> 24) & 0xFF));
    }

    public void WriteU64(ulong v)
    {
        for (int i = 0; i < 8; i++)
        {
            _buf.Add((byte)((v >> (i * 8)) & 0xFF));
        }
    }

    public void WriteString(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteU16((ushort)bytes.Length);
        _buf.AddRange(bytes);
    }
}

internal static class WireDecode
{
    public static (Opcode, byte) PeekHeader(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2) throw new InvalidDataException("frame too short for header");
        return ((Opcode)frame[0], frame[1]);
    }

    public static HelloFrame DecodeHello(ReadOnlySpan<byte> frame)
    {
        var r = new FrameReader(frame[2..]);
        var version = r.ReadU8();
        var mask = r.ReadU64();
        var count = r.ReadU16();
        var profiles = new List<string>(count);
        for (int i = 0; i < count; i++) profiles.Add(r.ReadString());
        return new HelloFrame(version, mask, profiles);
    }

    public static AllocFrame DecodeAlloc(ReadOnlySpan<byte> frame)
    {
        var r = new FrameReader(frame[2..]);
        return new AllocFrame(
            PadIndex: r.ReadU16(),
            ClientRelativeIndex: r.ReadU8(),
            Profile: r.ReadString(),
            ClientType: r.ReadU8(),
            Capabilities: r.ReadU16(),
            SupportedButtons: r.ReadU32());
    }

    public static ushort DecodeFree(ReadOnlySpan<byte> frame)
    {
        var r = new FrameReader(frame[2..]);
        return r.ReadU16();
    }

    public static StateFrame DecodeState(ReadOnlySpan<byte> frame)
    {
        var r = new FrameReader(frame[2..]);
        return new StateFrame(
            PadIndex: r.ReadU16(),
            ButtonFlags: r.ReadU32(),
            Lt: r.ReadU8(),
            Rt: r.ReadU8(),
            LsX: r.ReadI16(),
            LsY: r.ReadI16(),
            RsX: r.ReadI16(),
            RsY: r.ReadI16());
    }

    public static TouchFrame DecodeTouch(ReadOnlySpan<byte> frame)
    {
        var r = new FrameReader(frame[2..]);
        return new TouchFrame(
            PadIndex: r.ReadU16(),
            EventType: r.ReadU8(),
            PointerId: r.ReadU32(),
            X: r.ReadF32(),
            Y: r.ReadF32(),
            Pressure: r.ReadF32());
    }

    public static MotionFrame DecodeMotion(ReadOnlySpan<byte> frame)
    {
        var r = new FrameReader(frame[2..]);
        return new MotionFrame(
            PadIndex: r.ReadU16(),
            MotionType: r.ReadU8(),
            X: r.ReadF32(),
            Y: r.ReadF32(),
            Z: r.ReadF32());
    }

    public static BatteryFrame DecodeBattery(ReadOnlySpan<byte> frame)
    {
        var r = new FrameReader(frame[2..]);
        return new BatteryFrame(
            PadIndex: r.ReadU16(),
            State: r.ReadU8(),
            Percentage: r.ReadU8());
    }
}

internal static class WireEncode
{
    public static byte[] EncodeHello(HelloFrame h)
    {
        var w = new FrameWriter();
        w.WriteHeader(Opcode.Hello);
        w.WriteU8(h.ProtocolVersion);
        w.WriteU64(h.SupportedOpcodeMask);
        w.WriteU16((ushort)h.SupportedProfiles.Count);
        foreach (var p in h.SupportedProfiles) w.WriteString(p);
        return w.ToArray();
    }

    public static byte[] EncodeRumble(RumbleFrame r)
    {
        var w = new FrameWriter();
        w.WriteHeader(Opcode.Rumble);
        w.WriteU16(r.PadIndex);
        w.WriteU16(r.LowFreq);
        w.WriteU16(r.HighFreq);
        return w.ToArray();
    }

    public static byte[] EncodeLed(LedFrame l)
    {
        var w = new FrameWriter();
        w.WriteHeader(Opcode.Led);
        w.WriteU16(l.PadIndex);
        w.WriteU8(l.R);
        w.WriteU8(l.G);
        w.WriteU8(l.B);
        return w.ToArray();
    }
}
