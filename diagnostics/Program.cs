using System.Collections.Concurrent;
using BikeGame;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

static byte[] Read(IBuffer buffer) { using var reader = DataReader.FromBuffer(buffer); var bytes = new byte[buffer.Length]; reader.ReadBytes(bytes); return bytes; }
static void Log(string text) => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} {text}");
if (args.Contains("--app-session"))
{
    using var click = new Click2();
    var discovered = new TaskCompletionSource<ClickDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
    click.Discovered += d => discovered.TrySetResult(d);
    click.Status += s => Log($"APP STATUS {s}");
    click.Shift += delta => Log($"APP SHIFT {delta}");
    Log("APP SCAN"); click.Scan();
    ClickDevice selected;
    try { selected = await discovered.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
    catch (TimeoutException) { Log("NO CLICK 2 LEFT FOUND"); return; }
    click.StopScan();
    if (!await click.Connect(selected)) { Environment.ExitCode = 1; return; }
    Log("APP OBSERVE 90s"); await Task.Delay(90000);
    Log($"APP DONE; log={BluetoothLog.FilePath}"); return;
}
var found = new ConcurrentDictionary<ulong, (byte Type, BluetoothAddressType AddressType)>();
var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
watcher.Received += (_, e) =>
{
    foreach (var m in e.Advertisement.ManufacturerData)
    {
        if (m.CompanyId != 0x094a) continue;
        var bytes = Read(m.Data); if (bytes.Length == 0) continue;
        if (found.TryAdd(e.BluetoothAddress, (bytes[0], e.BluetoothAddressType)))
            Log($"ZWIFT {e.BluetoothAddress:X12} addressType={e.BluetoothAddressType} name={e.Advertisement.LocalName} rssi={e.RawSignalStrengthInDBm} manufacturer={Convert.ToHexString(bytes)} services={string.Join(',', e.Advertisement.ServiceUuids)}");
    }
};
watcher.Stopped += (_, e) => Log($"SCAN STOP {e.Error}");
Log("SCAN 12s"); watcher.Start(); await Task.Delay(12000); watcher.Stop();
if (args.Contains("--scan-only")) return;
var candidate = found.FirstOrDefault(x => x.Value.Type == 0x0b);
if (candidate.Key == 0) { Log("NO CLICK 2 LEFT FOUND"); return; }
Log($"CONNECT LEFT {candidate.Key:X12}");
using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(candidate.Key, candidate.Value.AddressType).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
if (device is null) { Log("NO DEVICE"); return; }
device.ConnectionStatusChanged += (d, _) => Log($"LINK {d.ConnectionStatus}");
using var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId); session.MaintainConnection = true;
var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(20));
Log($"SERVICES {services.Status}");
GattCharacteristic? rx = null;
var subscribed = new List<GattCharacteristic>();
foreach (var service in services.Services)
{
    Log($"SERVICE {service.Uuid}");
    var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(12));
    Log($"CHARS {chars.Status}");
    foreach (var characteristic in chars.Characteristics)
    {
        Log($"CHAR {characteristic.Uuid} {characteristic.CharacteristicProperties}");
        if (characteristic.Uuid == ZwiftProtocol.Rx) rx = characteristic;
        if (characteristic.Uuid.ToString().StartsWith("00002a26") || characteristic.Uuid.ToString().StartsWith("00002a24"))
        {
            var read = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached); Log($"READ {read.Status} {System.Text.Encoding.UTF8.GetString(Read(read.Value))}");
        }
        if (characteristic.Uuid != ZwiftProtocol.Async && characteristic.Uuid != ZwiftProtocol.Tx) continue;
        characteristic.ValueChanged += (c, e) => Log($"RX {c.Uuid} {Convert.ToHexString(Read(e.CharacteristicValue))}");
        var mode = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate) ? GattClientCharacteristicConfigurationDescriptorValue.Indicate : GattClientCharacteristicConfigurationDescriptorValue.Notify;
        Log($"SUBSCRIBE {characteristic.Uuid} {await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(mode)}");
        subscribed.Add(characteristic);
    }
}
if (args.Contains("--activate") && rx is not null)
{
    async Task Write(byte[] bytes)
    {
        using var writer = new DataWriter(); writer.WriteBytes(bytes);
        var mode = rx.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse) ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
        Log($"TX {Convert.ToHexString(bytes)} {await rx.WriteValueAsync(writer.DetachBuffer(), mode)}");
    }
    await Write("RideOn"u8.ToArray());
    await Task.Delay(500);
    if (args.Contains("--resume")) await Write([0xff, 4, 0]);
}
Log("OBSERVE 60s; press minus and plus"); await Task.Delay(60000);
foreach (var c in subscribed) await c.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None);
foreach (var s in services.Services) s.Dispose();
Log("DONE");
