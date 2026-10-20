namespace BikeGame;

public enum ClickReply { Other, RideOn, Buttons, VendorChallenge, LostControl }

// Wire format independently implemented from the public protocol references in THIRD_PARTY.md.
public static class ZwiftProtocol
{
    public static readonly Guid Service = Guid.Parse("00000001-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid TrainerService = Guid.Parse("0000fc82-0000-1000-8000-00805f9b34fb");
    public static readonly Guid Async = Guid.Parse("00000002-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid Rx = Guid.Parse("00000003-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid Tx = Guid.Parse("00000004-19ca-4651-86e5-fa29dcdd09d1");
    public static byte[] Handshake => [0x52, 0x69, 0x64, 0x65, 0x4f, 0x6e, 2, 3];
    public static ClickReply ControllerReply(byte[] frame)
    {
        if (TryButtons(frame, out _)) return ClickReply.Buttons;
        if (frame.AsSpan().StartsWith("RideOn"u8)) return ClickReply.RideOn;
        if (frame.Length >= 3 && frame[0] == 0xff && frame[1] == 3 && frame[2] == 0) return ClickReply.VendorChallenge;
        if (frame.Length == 1 && frame[0] == 0xfe) return ClickReply.LostControl;
        return ClickReply.Other; // FF05 is vendor status, not a reason to close BLE.
    }
    static readonly uint[] Ratios = [7500, 8700, 9900, 11100, 12300, 13800, 15300, 16800, 18600, 20400, 22200, 24000, 26099, 28200, 30300, 32400, 34900, 37400, 39900, 42399, 45400, 48400, 51400, 54899];
    static void Varint(List<byte> output, uint value)
    {
        while (value >= 128) { output.Add((byte)((value & 127) | 128)); value >>= 7; }
        output.Add((byte)value);
    }
    public static uint Ratio(int gear) => gear is >= 1 and <= 24 ? Ratios[gear - 1] : throw new ArgumentOutOfRangeException(nameof(gear));
    public static byte[] Gear(int gear, uint sequence)
    {
        uint ratio = gear == 0 ? 0 : Ratio(gear); // zero explicitly disables virtual gearing
        var payload = new List<byte> { 0x38 }; Varint(payload, ratio);
        var command = new List<byte> { 0x3f, 0x08, 0x01, 0x10 }; Varint(command, sequence);
        command.Add(0x22); Varint(command, (uint)payload.Count); command.AddRange(payload);
        return command.ToArray();
    }
    static bool ReadVarint(byte[] bytes, ref int offset, out uint value)
    {
        value = 0;
        for (int shift = 0; shift <= 28 && offset < bytes.Length; shift += 7)
        {
            byte b = bytes[offset++];
            if (shift == 28 && (b & 0xf0) != 0) return false;
            value |= (uint)(b & 127) << shift;
            if (b < 128) return true;
        }
        return false;
    }
    public static bool TryButtons(byte[] frame, out uint bitmap)
    {
        bitmap = uint.MaxValue;
        if (frame.Length < 2 || frame[0] != 0x23) return false;
        int p = 1;
        while (p < frame.Length)
        {
            if (!ReadVarint(frame, ref p, out uint tag) || tag >> 3 == 0) return false;
            switch (tag & 7)
            {
                case 0:
                    if (!ReadVarint(frame, ref p, out uint value)) return false;
                    if (tag >> 3 == 1) { bitmap = value; return true; }
                    break;
                case 1: p += 8; break;
                case 2:
                    if (!ReadVarint(frame, ref p, out uint length) || length > frame.Length - p) return false;
                    p += (int)length; break;
                case 5: p += 4; break;
                default: return false;
            }
            if (p > frame.Length) return false;
        }
        return false;
    }
}

public sealed class ClickButtonState
{
    uint previous = uint.MaxValue;
    public int UpdatePair(byte[] packet)
    {
        if (!ZwiftProtocol.TryButtons(packet, out var bitmap)) return 0;
        uint pressed = previous & ~bitmap;
        previous = bitmap;
        bool minus = (pressed & 0x100) != 0, plus = (pressed & 0x1000) != 0;
        return minus == plus ? 0 : minus ? -1 : 1;
    }
    public int Update(byte[] packet, bool left)
    {
        if (!ZwiftProtocol.TryButtons(packet, out var bitmap)) return 0;
        uint mask = left ? 0x100u : 0x1000u;
        bool pressed = (previous & mask) != 0 && (bitmap & mask) == 0;
        previous = bitmap;
        return pressed ? (left ? -1 : 1) : 0;
    }
}
