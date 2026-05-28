// Manages HMController instances on behalf of incoming IPC frames.
//
// The class compiles in two configurations:
//   * With HIDMaestro.Core.dll present (HIDMAESTRO_CORE defined): we instantiate
//     real HMContext and HMController objects and forward state into them.
//   * Without the DLL: we keep the per-pad state and log activity, so the IPC
//     plumbing can be exercised end-to-end without HIDMaestro installed.
//
// The host-bound feedback callback emits RUMBLE / LED / TRIGGER_EFFECT frames
// back through the pipe. It runs on HIDMaestro's polling thread (~8 ms) per
// the SDK contract, so it must be cheap — we serialize to bytes and hand them
// to the writer.

#if HIDMAESTRO_CORE
using HIDMaestro;
#endif

namespace VibePollo.HidMaestroHost;

internal sealed class ControllerManager : IDisposable
{
    private readonly Func<byte[], Task> _send;

#if HIDMAESTRO_CORE
    private readonly HMContext? _ctx;
#endif

    private sealed class PadInstance
    {
        public string Profile = string.Empty;
        public byte ClientRelativeIndex;

        // Latest input state. SubmitState builds an HMGamepadState from these.
        public uint ButtonFlags;
        public byte Lt;
        public byte Rt;
        public short LsX;
        public short LsY;
        public short RsX;
        public short RsY;

        public ushort Finger0X;
        public ushort Finger0Y;
        public bool Finger0Active;
        public byte Finger0TrackingId;
        public ushort Finger1X;
        public ushort Finger1Y;
        public bool Finger1Active;
        public byte Finger1TrackingId;
        public uint TouchpadPacketCounter;

        public short GyroPitch;
        public short GyroYaw;
        public short GyroRoll;
        public short AccelX;
        public short AccelY;
        public short AccelZ;

        public byte BatteryLevel;
        public bool BatteryCharging;
        public bool BatteryFull;

#if HIDMAESTRO_CORE
        public HMController? Controller;
#endif
    }

    private readonly Dictionary<ushort, PadInstance> _pads = new();
    private readonly object _lock = new();

    public ControllerManager(Func<byte[], Task> sendFrame)
    {
        _send = sendFrame;

#if HIDMAESTRO_CORE
        try
        {
            _ctx = new HMContext();
            _ctx.LoadDefaultProfiles();
            if (!_ctx.IsDriverInstalled)
            {
                Console.Error.WriteLine("[hidmaestro-host] HIDMaestro driver not installed; attempting install (requires elevation)");
                _ctx.InstallDriver();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[hidmaestro-host] HMContext init failed: {ex.Message}");
            _ctx = null;
        }
#endif
    }

    public IReadOnlyList<string> SupportedProfiles
    {
        get
        {
#if HIDMAESTRO_CORE
            return _ctx?.AllProfiles.Select(p => p.Id).ToArray() ?? Array.Empty<string>();
#else
            return Array.Empty<string>();
#endif
        }
    }

    public void Alloc(AllocFrame frame)
    {
        lock (_lock)
        {
            if (_pads.ContainsKey(frame.PadIndex))
            {
                Console.Error.WriteLine($"[hidmaestro-host] pad {frame.PadIndex} already allocated; reusing");
                _pads.Remove(frame.PadIndex);
            }
            var pad = new PadInstance
            {
                Profile = frame.Profile,
                ClientRelativeIndex = frame.ClientRelativeIndex,
            };

#if HIDMAESTRO_CORE
            try
            {
                var profile = _ctx?.GetProfile(frame.Profile);
                if (profile is null)
                {
                    Console.Error.WriteLine($"[hidmaestro-host] unknown HIDMaestro profile '{frame.Profile}'");
                }
                else
                {
                    pad.Controller = _ctx!.CreateController(profile);
                    pad.Controller.OutputReceived += (c, packet) => OnOutputReceived(frame.PadIndex, packet);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[hidmaestro-host] alloc failed for profile '{frame.Profile}': {ex.Message}");
            }
#else
            Console.Error.WriteLine($"[hidmaestro-host] alloc pad={frame.PadIndex} profile='{frame.Profile}' (HIDMaestro.Core not linked; no-op)");
#endif
            _pads[frame.PadIndex] = pad;
        }
    }

    public void Free(ushort padIndex)
    {
        lock (_lock)
        {
            if (!_pads.TryGetValue(padIndex, out var pad))
            {
                return;
            }
            _pads.Remove(padIndex);

#if HIDMAESTRO_CORE
            pad.Controller?.Dispose();
#endif
        }
    }

    public void State(StateFrame frame)
    {
        lock (_lock)
        {
            if (!_pads.TryGetValue(frame.PadIndex, out var pad))
            {
                return;
            }
            pad.ButtonFlags = frame.ButtonFlags;
            pad.Lt = frame.Lt;
            pad.Rt = frame.Rt;
            pad.LsX = frame.LsX;
            pad.LsY = frame.LsY;
            pad.RsX = frame.RsX;
            pad.RsY = frame.RsY;
            Submit(pad);
        }
    }

    public void Touch(TouchFrame frame)
    {
        lock (_lock)
        {
            if (!_pads.TryGetValue(frame.PadIndex, out var pad))
            {
                return;
            }
            // Two pointers max (DS4 touchpad shape). Pointer slot allocation
            // mirrors vigem_t::touch: low slot, then high slot, by tracking id.
            var down = frame.EventType == LiTouchEvent.Down;
            var up = frame.EventType == LiTouchEvent.Up || frame.EventType == LiTouchEvent.Cancel;
            var cancelAll = frame.EventType == LiTouchEvent.CancelAll;

            if (cancelAll)
            {
                pad.Finger0Active = false;
                pad.Finger1Active = false;
                pad.TouchpadPacketCounter++;
                Submit(pad);
                return;
            }

            // Choose a slot: existing match by tracking id, else first free.
            var fingerId = (byte)(frame.PointerId & 0xFF);
            int slot = pad.Finger0Active && pad.Finger0TrackingId == fingerId ? 0
                     : pad.Finger1Active && pad.Finger1TrackingId == fingerId ? 1
                     : !pad.Finger0Active ? 0
                     : !pad.Finger1Active ? 1
                     : -1;
            if (slot < 0) return;

            ushort x = (ushort)Math.Clamp((int)(frame.X * 1919), 0, 1919);
            ushort y = (ushort)Math.Clamp((int)(frame.Y * 1079), 0, 1079);

            if (slot == 0)
            {
                pad.Finger0X = x;
                pad.Finger0Y = y;
                pad.Finger0TrackingId = fingerId;
                pad.Finger0Active = !up;
            }
            else
            {
                pad.Finger1X = x;
                pad.Finger1Y = y;
                pad.Finger1TrackingId = fingerId;
                pad.Finger1Active = !up;
            }

            pad.TouchpadPacketCounter++;
            Submit(pad);
        }
    }

    public void Motion(MotionFrame frame)
    {
        lock (_lock)
        {
            if (!_pads.TryGetValue(frame.PadIndex, out var pad))
            {
                return;
            }
            // Vibepollo sends accel in m/s^2 and gyro in deg/s. HIDMaestro's
            // HMGamepadState consumes int16 in the device's native scale. The
            // SDK's StandardAxes helper does the conversion for sticks; we
            // pass through the IMU raw — accept that until we have a Steam-
            // Controller-specific profile that documents its scale.
            if (frame.MotionType == LiMotionType.Accel)
            {
                pad.AccelX = (short)Math.Clamp(frame.X, short.MinValue, short.MaxValue);
                pad.AccelY = (short)Math.Clamp(frame.Y, short.MinValue, short.MaxValue);
                pad.AccelZ = (short)Math.Clamp(frame.Z, short.MinValue, short.MaxValue);
            }
            else if (frame.MotionType == LiMotionType.Gyro)
            {
                pad.GyroPitch = (short)Math.Clamp(frame.X, short.MinValue, short.MaxValue);
                pad.GyroYaw = (short)Math.Clamp(frame.Y, short.MinValue, short.MaxValue);
                pad.GyroRoll = (short)Math.Clamp(frame.Z, short.MinValue, short.MaxValue);
            }
            Submit(pad);
        }
    }

    public void Battery(BatteryFrame frame)
    {
        lock (_lock)
        {
            if (!_pads.TryGetValue(frame.PadIndex, out var pad))
            {
                return;
            }
            pad.BatteryLevel = (byte)(frame.Percentage / 10);
            pad.BatteryCharging = frame.State == LiBatteryState.Charging;
            pad.BatteryFull = frame.State == LiBatteryState.Full;
            Submit(pad);
        }
    }

    private void Submit(PadInstance pad)
    {
#if HIDMAESTRO_CORE
        if (pad.Controller is null) return;

        // Sticks/triggers normalize to [0,1]/[-1,1] per HIDMaestro's helper.
        float lsX = pad.LsX / 32768f;
        float lsY = pad.LsY / 32768f;
        float rsX = pad.RsX / 32768f;
        float rsY = pad.RsY / 32768f;
        float lt = pad.Lt / 255f;
        float rt = pad.Rt / 255f;

        var state = new HMGamepadState
        {
            Axes = HMGamepadState.StandardAxes(pad.Controller.Profile, lsX, lsY, rsX, rsY, lt, rt),
            Buttons = MapButtons(pad.ButtonFlags),
            Hat = MapDpadHat(pad.ButtonFlags),
            Finger0Active = pad.Finger0Active,
            Finger0X = pad.Finger0X,
            Finger0Y = pad.Finger0Y,
            Finger0TrackingId = pad.Finger0TrackingId,
            Finger1Active = pad.Finger1Active,
            Finger1X = pad.Finger1X,
            Finger1Y = pad.Finger1Y,
            Finger1TrackingId = pad.Finger1TrackingId,
            TouchpadPacketCounter = pad.TouchpadPacketCounter,
            GyroPitch = pad.GyroPitch,
            GyroYaw = pad.GyroYaw,
            GyroRoll = pad.GyroRoll,
            AccelX = pad.AccelX,
            AccelY = pad.AccelY,
            AccelZ = pad.AccelZ,
            BatteryLevel = pad.BatteryLevel,
            BatteryCharging = pad.BatteryCharging,
            BatteryFull = pad.BatteryFull,
        };
        pad.Controller.SubmitState(in state);
#endif
    }

#if HIDMAESTRO_CORE
    private static HMButton MapButtons(uint flags)
    {
        var f = (LiButton)flags;
        HMButton b = 0;
        if (f.HasFlag(LiButton.A)) b |= HMButton.A;
        if (f.HasFlag(LiButton.B)) b |= HMButton.B;
        if (f.HasFlag(LiButton.X)) b |= HMButton.X;
        if (f.HasFlag(LiButton.Y)) b |= HMButton.Y;
        if (f.HasFlag(LiButton.LeftButton)) b |= HMButton.LeftBumper;
        if (f.HasFlag(LiButton.RightButton)) b |= HMButton.RightBumper;
        if (f.HasFlag(LiButton.Back)) b |= HMButton.Back;
        if (f.HasFlag(LiButton.Start)) b |= HMButton.Start;
        if (f.HasFlag(LiButton.LeftStick)) b |= HMButton.LeftStick;
        if (f.HasFlag(LiButton.RightStick)) b |= HMButton.RightStick;
        if (f.HasFlag(LiButton.Home) || f.HasFlag(LiButton.MiscButton)) b |= HMButton.Guide;
        if (f.HasFlag(LiButton.TouchpadButton)) b |= HMButton.Touchpad;
        return b;
    }

    private static HMHat MapDpadHat(uint flags)
    {
        var f = (LiButton)flags;
        var up = f.HasFlag(LiButton.DpadUp);
        var down = f.HasFlag(LiButton.DpadDown);
        var left = f.HasFlag(LiButton.DpadLeft);
        var right = f.HasFlag(LiButton.DpadRight);
        if (up && right) return HMHat.NorthEast;
        if (up && left) return HMHat.NorthWest;
        if (down && right) return HMHat.SouthEast;
        if (down && left) return HMHat.SouthWest;
        if (up) return HMHat.North;
        if (down) return HMHat.South;
        if (right) return HMHat.East;
        if (left) return HMHat.West;
        return HMHat.None;
    }
#endif

    private void OnOutputReceived(ushort padIndex, object packet)
    {
        // packet is HMOutputPacket; deferred to a real handler when the SDK
        // surface is linked. For now, forward the raw bytes back as a RUMBLE
        // when we can detect an XInput-shaped payload (5 bytes, [type, big,
        // small, ...]). Anything else is dropped.
#if HIDMAESTRO_CORE
        if (packet is HMOutputPacket pkt && pkt.Source == HMOutputSource.XInput && pkt.Data.Length >= 3)
        {
            var data = pkt.Data.Span;
            // XInput vibration frame: typically 5 bytes — let's pull motors.
            // (Some SDK builds use byte 1 = large motor, byte 2 = small motor.)
            byte big = data.Length >= 4 ? data[3] : data[1];
            byte small = data.Length >= 5 ? data[4] : data[2];
            var frame = WireEncode.EncodeRumble(new RumbleFrame(
                PadIndex: padIndex,
                LowFreq: (ushort)(big << 8),
                HighFreq: (ushort)(small << 8)));
            _ = _send(frame);
        }
#endif
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var pad in _pads.Values)
            {
#if HIDMAESTRO_CORE
                pad.Controller?.Dispose();
#endif
            }
            _pads.Clear();
#if HIDMAESTRO_CORE
            _ctx?.Dispose();
#endif
        }
    }
}
