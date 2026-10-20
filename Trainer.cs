using System.Buffers.Binary;
using System.IO;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BikeGame;

public sealed record TrainerDevice(ulong Address, string Name)
{
    public override string ToString() => $"{Name} · {Address:X12}";
}

public sealed class Trainer : IDisposable
{
    static Guid Uuid(int value) => Guid.Parse($"0000{value:x4}-0000-1000-8000-00805f9b34fb");
    BluetoothLEAdvertisementWatcher? watcher;
    BluetoothLEDevice? device;
    GattDeviceService? service;
    GattSession? session;
    GattCharacteristic? data, control, status;
    GattDeviceService? shiftService;
    GattCharacteristic? shiftControl;
    bool shiftReady;
    uint shiftSequence;
    public bool SupportsVirtualShifting => shiftControl is not null;
    public int? CurrentGear { get; private set; }
    public string VirtualShiftInfo { get; private set; } = "Sem ligação ao rolo";
    readonly SemaphoreSlim commands = new(1, 1);
    TaskCompletionSource<byte>? pending;
    byte pendingOpcode;
    bool hasControl;
    public bool Connected => device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
    public bool SupportsSimulation { get; private set; }
    public bool SupportsPower { get; private set; }
    public event Action<TrainerDevice>? Discovered;
    public event Action<Telemetry>? Measurement;
    public event Action<string>? Lost;
    public void Scan()
    {
        StopScan();
        watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += Advertisement;
        watcher.Start();
    }
    void Advertisement(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var name = args.Advertisement.LocalName;
        if (args.Advertisement.ServiceUuids.Contains(Uuid(0x1826)) || name.Contains("KICKR", StringComparison.OrdinalIgnoreCase) || name.Contains("WAHOO", StringComparison.OrdinalIgnoreCase))
            Discovered?.Invoke(new(args.BluetoothAddress, string.IsNullOrWhiteSpace(name) ? "Rolo FTMS" : name));
    }
    public void StopScan() { if (watcher is not null) { watcher.Received -= Advertisement; watcher.Stop(); watcher = null; } }
    static byte[] Bytes(IBuffer buffer) { var bytes = new byte[buffer.Length]; using var reader = DataReader.FromBuffer(buffer); reader.ReadBytes(bytes); return bytes; }
    static void Check(GattCommunicationStatus result) { if (result != GattCommunicationStatus.Success) throw new IOException($"Bluetooth: {result}. Aproxima o rolo e fecha outras apps que o controlem."); }
    async Task<GattCharacteristic?> Characteristic(int uuid)
    {
        var result = await service!.GetCharacteristicsForUuidAsync(Uuid(uuid), BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(12));
        Check(result.Status); return result.Characteristics.FirstOrDefault();
    }
    public async Task Connect(ulong address)
    {
        Dispose();
        try
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask().WaitAsync(TimeSpan.FromSeconds(15))
                ?? throw new IOException("Não foi possível abrir o dispositivo Bluetooth.");
            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
            if (session is not null) session.MaintainConnection = true;
            var result = await device.GetGattServicesForUuidAsync(Uuid(0x1826), BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            Check(result.Status);
            service = result.Services.FirstOrDefault() ?? throw new IOException("O dispositivo não expõe o serviço FTMS (1826).");
            data = await Characteristic(0x2AD2) ?? throw new IOException("Dados Indoor Bike indisponíveis.");
            data.ValueChanged += OnData;
            Check(await data.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            control = await Characteristic(0x2AD9);
            if (control is not null)
            {
                control.ValueChanged += OnControl;
                Check(await control.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Indicate).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            }
            status = await Characteristic(0x2ADA);
            if (status is not null)
            {
                status.ValueChanged += OnStatus;
                Check(await status.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            }
            var feature = await Characteristic(0x2ACC);
            if (feature is not null)
            {
                var read = await feature.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(10)); Check(read.Status);
                var bytes = Bytes(read.Value);
                if (bytes.Length >= 8)
                {
                    uint targets = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
                    SupportsSimulation = control is not null && (targets & (1 << 13)) != 0;
                    SupportsPower = control is not null && (targets & (1 << 3)) != 0;
                }
            }
            device.ConnectionStatusChanged += OnConnection;
            // This optional service must not prevent ordinary FTMS telemetry/control.
            try
            {
                var shifts = await device.GetGattServicesForUuidAsync(ZwiftProtocol.TrainerService, BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(12));
                Check(shifts.Status); shiftService = shifts.Services.FirstOrDefault();
                if (shiftService is not null)
                {
                    var chars = await shiftService.GetCharacteristicsForUuidAsync(ZwiftProtocol.Rx, BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                    Check(chars.Status); shiftControl = chars.Characteristics.FirstOrDefault();
                }
                VirtualShiftInfo = SupportsVirtualShifting ? "Mudanças disponíveis · ativa para começar" : "O rolo não expõe o serviço de mudanças FC82";
            }
            catch (Exception ex) { VirtualShiftInfo = $"Não foi possível detetar mudanças: {ex.Message}"; }
        }
        catch { Dispose(); throw; }
    }
    void OnData(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try { Measurement?.Invoke(Ftms.ParseBikeData(Bytes(args.CharacteristicValue))); }
        catch (FormatException) { /* Ignore incomplete packets; the UI detects stale telemetry. */ }
    }
    void OnControl(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = Bytes(args.CharacteristicValue);
        if (bytes.Length >= 3 && bytes[0] == 0x80 && bytes[1] == pendingOpcode) pending?.TrySetResult(bytes[2]);
    }
    void OnStatus(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = Bytes(args.CharacteristicValue);
        if (bytes.Length > 0 && bytes[0] == 0xff) { hasControl = false; Lost?.Invoke("O rolo retirou a permissão de controlo. Volta a ligar."); }
    }
    void OnConnection(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        { hasControl = false; pending?.TrySetException(new IOException("Ligação Bluetooth perdida.")); Lost?.Invoke("Ligação perdida. Percurso pausado; volta a ligar para continuar."); }
    }
    async Task Send(byte[] bytes)
    {
        if (control is null || !Connected) throw new IOException("Liga um rolo com controlo FTMS.");
        pendingOpcode = bytes[0];
        var response = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = response;
        try
        {
            using var writer = new DataWriter(); writer.WriteBytes(bytes);
            Check(await control.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse).AsTask().WaitAsync(TimeSpan.FromSeconds(8)));
            var code = await response.Task.WaitAsync(TimeSpan.FromSeconds(8));
            if (code != 1) throw new IOException($"O rolo recusou o comando 0x{bytes[0]:X2} (FTMS {code}: {code switch { 2 => "não suportado", 3 => "parâmetro inválido", 4 => "operação falhou", 5 => "controlo não permitido", _ => "erro" }}).");
        }
        catch { hasControl = false; throw; }
        finally { pending = null; }
    }
    async Task WriteShift(byte[] bytes)
    {
        if (shiftControl is null || !Connected) throw new IOException("Serviço de mudanças virtuais indisponível no rolo.");
        using var writer = new DataWriter(); writer.WriteBytes(bytes);
        var option = shiftControl.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        Check(await shiftControl.WriteValueAsync(writer.DetachBuffer(), option).AsTask().WaitAsync(TimeSpan.FromSeconds(8)));
    }
    async Task SetGearCore(int gear)
    {
        if (!shiftReady)
        {
            await WriteShift(ZwiftProtocol.Handshake); await Task.Delay(150); shiftReady = true;
        }
        await WriteShift(ZwiftProtocol.Gear(gear, shiftSequence++));
        CurrentGear = gear == 0 ? null : gear;
    }
    public async Task SetGear(int gear)
    {
        _ = ZwiftProtocol.Ratio(gear);
        await commands.WaitAsync();
        try
        {
            if (!hasControl) throw new InvalidOperationException("Aplica uma inclinação ou inicia o percurso antes de ativar as mudanças.");
            await SetGearCore(gear);
        }
        finally { commands.Release(); }
    }
    public async Task SetGrade(double grade)
    {
        if (!SupportsSimulation) throw new IOException("O rolo não anuncia suporte a simulação de inclinação.");
        await commands.WaitAsync();
        try
        {
            if (!hasControl) { await Send([0]); hasControl = true; await Send([7]); }
            await Send(Ftms.Simulation(grade));
            // SIM/FTMS mode transitions can reset the trainer's virtual gearing.
            if (CurrentGear is int gear) await SetGearCore(gear);
        }
        finally { commands.Release(); }
    }
    public async Task SetPower(int watts)
    {
        if (!SupportsPower) throw new IOException("O rolo não anuncia suporte a potência alvo.");
        await commands.WaitAsync();
        try
        {
            if (!hasControl) { await Send([0]); hasControl = true; await Send([7]); }
            if (CurrentGear.HasValue) await SetGearCore(0);
            await Send(Ftms.Power(watts));
        }
        finally { commands.Release(); }
    }
    public async Task Stop()
    {
        await commands.WaitAsync();
        try
        {
            if (hasControl && Connected)
            {
                try { if (CurrentGear.HasValue) await SetGearCore(0); }
                finally { await Send([0x08, 0x01]); }
            }
        }
        finally { hasControl = false; CurrentGear = null; shiftReady = false; commands.Release(); }
    }
    public void Dispose()
    {
        StopScan(); hasControl = false; SupportsPower = SupportsSimulation = false;
        CurrentGear = null; shiftReady = false; shiftSequence = 0;
        shiftControl = null; shiftService?.Dispose(); shiftService = null;
        VirtualShiftInfo = "Sem ligação ao rolo";
        if (data is not null) data.ValueChanged -= OnData;
        if (control is not null) control.ValueChanged -= OnControl;
        if (status is not null) status.ValueChanged -= OnStatus;
        pending?.TrySetException(new IOException("Ligação encerrada.")); pending = null;
        if (device is not null) device.ConnectionStatusChanged -= OnConnection;
        session?.Dispose(); session = null;
        service?.Dispose(); service = null; device?.Dispose(); device = null; data = control = status = null;
    }
}
