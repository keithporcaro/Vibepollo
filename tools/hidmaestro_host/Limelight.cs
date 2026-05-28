// Mirror of the relevant flags from moonlight-common-c's Limelight.h. Kept here
// (rather than P/Invoked) because the values are a stable wire contract that
// must match Vibepollo bit-for-bit.

namespace VibePollo.HidMaestroHost;

[Flags]
internal enum LiButton : uint
{
    DpadUp = 0x0001,
    DpadDown = 0x0002,
    DpadLeft = 0x0004,
    DpadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftStick = 0x0040,
    RightStick = 0x0080,
    LeftButton = 0x0100,   // LB / L1
    RightButton = 0x0200,  // RB / R1
    Home = 0x0400,         // Guide / PS
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
    Paddle1 = 0x010000,
    Paddle2 = 0x020000,
    Paddle3 = 0x040000,
    Paddle4 = 0x080000,
    TouchpadButton = 0x100000,
    MiscButton = 0x200000,
}

internal static class LiCType
{
    public const byte Unknown = 0x00;
    public const byte Xbox = 0x01;
    public const byte PlayStation = 0x02;
    public const byte Nintendo = 0x03;
}

internal static class LiMotionType
{
    public const byte Accel = 0x01;
    public const byte Gyro = 0x02;
}

internal static class LiTouchEvent
{
    public const byte Hover = 0x00;
    public const byte Down = 0x01;
    public const byte Up = 0x02;
    public const byte Move = 0x03;
    public const byte Cancel = 0x04;
    public const byte CancelAll = 0x05;
    public const byte HoverLeave = 0x06;
    public const byte ButtonOnly = 0x07;
}

internal static class LiBatteryState
{
    public const byte Unknown = 0x00;
    public const byte NotPresent = 0x01;
    public const byte Discharging = 0x02;
    public const byte Charging = 0x03;
    public const byte NotCharging = 0x04;
    public const byte Full = 0x05;
}
