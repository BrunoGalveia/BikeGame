using System.IO;

namespace BikeGame;

public static class BluetoothLog
{
    static readonly object gate = new();
    public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "logs", $"click2-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    public static void Write(string text)
    {
        lock (gate)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.AppendAllText(FilePath, $"{DateTimeOffset.Now:O} {text}{Environment.NewLine}"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    public static void Frame(string label, byte[] bytes) => Write($"{label} len={bytes.Length} {Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 32)))}{(bytes.Length > 32 ? "…" : "")}");
}
