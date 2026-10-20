using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BikeGame;

public sealed record Segment([property: JsonRequired] string Name, [property: JsonRequired] double DistanceMeters, [property: JsonRequired] double GradePercent);
public sealed record Route([property: JsonRequired] int Version, [property: JsonRequired] string Name, [property: JsonRequired] Segment[] Segments)
{
    [JsonIgnore] public double TotalMeters => Segments.Sum(s => s.DistanceMeters);
    public static Route Parse(string json)
    {
        var route = JsonSerializer.Deserialize<Route>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new FormatException("O JSON está vazio.");
        if (route.Version != 1) throw new FormatException("A versão do percurso deve ser 1.");
        if (string.IsNullOrWhiteSpace(route.Name)) throw new FormatException("Indica o nome do percurso.");
        if (route.Segments is not { Length: > 0 and <= 10000 }) throw new FormatException("Indica entre 1 e 10000 segmentos.");
        foreach (var s in route.Segments)
        {
            if (s is null || string.IsNullOrWhiteSpace(s.Name)) throw new FormatException("Cada segmento precisa de nome.");
            if (!double.IsFinite(s.DistanceMeters) || s.DistanceMeters <= 0 || s.DistanceMeters > 1000000)
                throw new FormatException($"Distância inválida em {s.Name}: usa metros, acima de zero.");
            if (!double.IsFinite(s.GradePercent) || s.GradePercent is < -16 or > 16)
                throw new FormatException($"Inclinação inválida em {s.Name}: usa -16 a 16%.");
        }
        return route;
    }
    public int SegmentAt(double meters)
    {
        double end = 0;
        for (int i = 0; i < Segments.Length; i++) { end += Segments[i].DistanceMeters; if (meters < end) return i; }
        return Segments.Length - 1;
    }
}

public sealed class Ride
{
    public Route Route { get; }
    public double Meters { get; private set; }
    public double Seconds { get; private set; }
    public bool Running { get; set; }
    public bool Complete => Meters >= Route.TotalMeters;
    public double Percent => Math.Clamp(Meters / Route.TotalMeters * 100, 0, 100);
    public int SegmentIndex => Route.SegmentAt(Meters);
    public Ride(Route route) => Route = route;
    public void Advance(double speedKph, double seconds)
    {
        if (!Running || Complete || !double.IsFinite(seconds) || seconds <= 0) return;
        Seconds += seconds;
        if (double.IsFinite(speedKph) && speedKph > 0) Meters = Math.Min(Route.TotalMeters, Meters + speedKph / 3.6 * seconds);
        if (Complete) Running = false;
    }
}

public sealed record Telemetry(double? SpeedKph = null, double? CadenceRpm = null, int? PowerWatts = null, int? HeartRate = null);

public static class Ftms
{
    public static Telemetry ParseBikeData(byte[] bytes)
    {
        var p = 2;
        if (bytes.Length < 2) throw new FormatException("Pacote FTMS truncado.");
        int flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        int Read(int count)
        {
            if (p + count > bytes.Length) throw new FormatException("Pacote FTMS truncado.");
            int value = count == 1 ? bytes[p] : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p));
            p += count; return value;
        }
        void Skip(int count) { if (p + count > bytes.Length) throw new FormatException("Pacote FTMS truncado."); p += count; }
        double? speed = (flags & 1) == 0 ? Read(2) / 100.0 : null;
        if ((flags & 2) != 0) Skip(2);
        double? cadence = (flags & 4) != 0 ? Read(2) / 2.0 : null;
        if ((flags & 8) != 0) Skip(2);
        if ((flags & 16) != 0) Skip(3);
        if ((flags & 32) != 0) Skip(2);
        int? power = (flags & 64) != 0 ? (short)Read(2) : null;
        if ((flags & 128) != 0) Skip(2);
        if ((flags & 256) != 0) Skip(5);
        int? hr = (flags & 512) != 0 ? Read(1) : null;
        if ((flags & 1024) != 0) Skip(1);
        if ((flags & 2048) != 0) Skip(2);
        if ((flags & 4096) != 0) Skip(2);
        return new(speed, cadence, power, hr);
    }
    public static byte[] Simulation(double grade)
    {
        if (!double.IsFinite(grade) || grade is < -16 or > 16) throw new ArgumentOutOfRangeException(nameof(grade));
        var bytes = new byte[] { 0x11, 0, 0, 0, 0, 40, 51 };
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(3), (short)Math.Round(grade * 100));
        return bytes; // wind 0 m/s; grade 0.01%; Crr 0.004; Cw 0.51 kg/m
    }
    public static byte[] Power(int watts)
    {
        if (watts is < 0 or > 1800) throw new ArgumentOutOfRangeException(nameof(watts));
        var bytes = new byte[3]; bytes[0] = 5;
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(1), (short)watts); return bytes;
    }
}
