using System.IO;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BikeGame;

public sealed record ClickDevice(ulong Address, BluetoothAddressType AddressType)
{
    public string Name => $"Zwift Click 2 · par via esquerdo · {Address:X12}";
}

// Normal Click 2 mode: one BLE connection to the left; both buttons in one bitmap.
public sealed class Click2 : IDisposable
{
    BluetoothLEAdvertisementWatcher? watcher;
    Session? session;
    readonly HashSet<ulong> seen = new();
    public event Action<ClickDevice>? Discovered;
    public event Action<int>? Shift;
    public event Action<string>? Status;
    public event Action<bool>? ButtonObserved;
    public void Scan()
    {
        StopScan(); lock (seen) seen.Clear();
        watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += Advertisement; watcher.Stopped += ScanStopped; watcher.Start();
        BluetoothLog.Write("SCAN started; selecting aggregate left endpoint only");
    }
    void ScanStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs e)
    {
        BluetoothLog.Write($"SCAN stopped: {e.Error}");
        if (e.Error != BluetoothError.Success) Status?.Invoke($"Pesquisa Bluetooth interrompida: {e.Error}");
    }
    void Advertisement(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        foreach (var manufacturer in e.Advertisement.ManufacturerData)
        {
            if (manufacturer.CompanyId != 0x094a || manufacturer.Data.Length == 0) continue;
            var bytes = Read(manufacturer.Data);
            lock (seen)
                if (seen.Add(e.BluetoothAddress)) BluetoothLog.Write($"ADV {e.BluetoothAddress:X12} type={e.BluetoothAddressType} rssi={e.RawSignalStrengthInDBm} manufacturer={Convert.ToHexString(bytes)} services={string.Join(',', e.Advertisement.ServiceUuids)}");
            if (bytes[0] == 0x0b) Discovered?.Invoke(new(e.BluetoothAddress, e.BluetoothAddressType));
        }
    }
    public void StopScan()
    {
        if (watcher is null) return;
        watcher.Received -= Advertisement; watcher.Stopped -= ScanStopped; watcher.Stop(); watcher = null;
    }
    public async Task<bool> Connect(ClickDevice device)
    {
        session?.Dispose();
        var current = new Session(device, delta => Shift?.Invoke(delta), text => Status?.Invoke(text), left => ButtonObserved?.Invoke(left));
        session = current;
        try { await current.Connect(); return true; }
        catch (Exception ex)
        {
            BluetoothLog.Write($"CONNECT ERROR {ex}"); current.Dispose();
            if (ReferenceEquals(session, current)) session = null;
            Status?.Invoke($"Ligação falhou ({current.Stage}): {ex.Message}"); return false;
        }
    }
    public void Dispose() { StopScan(); session?.Dispose(); session = null; }
    static byte[] Read(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length]; using var reader = DataReader.FromBuffer(buffer); reader.ReadBytes(bytes); return bytes;
    }
    static void Check(GattCommunicationStatus status)
    {
        if (status != GattCommunicationStatus.Success) throw new IOException($"Bluetooth: {status}. Acorda o comando esquerdo e volta a ligar.");
    }
    sealed class Session : IDisposable
    {
        readonly ClickDevice selected;
        readonly Action<int> shift;
        readonly Action<string> report;
        readonly Action<bool> observed;
        readonly ClickButtonState buttons = new();
        readonly CancellationTokenSource cancellation = new();
        BluetoothLEDevice? device;
        GattSession? gatt;
        GattDeviceService? service;
        GattCharacteristic? rx, tx, notifications;
        volatile bool disposed, receivingButtons, connected, challengeSeen;
        public string Stage { get; private set; } = "abrir dispositivo";
        public Session(ClickDevice selected, Action<int> shift, Action<string> report, Action<bool> observed)
        { this.selected = selected; this.shift = shift; this.report = report; this.observed = observed; }
        public async Task Connect()
        {
            report("A ligar ao par Click 2 através do lado esquerdo…");
            BluetoothLog.Write($"CONNECT {selected.Address:X12} type={selected.AddressType}");
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(selected.Address, selected.AddressType).AsTask().WaitAsync(TimeSpan.FromSeconds(15))
                ?? throw new IOException("Comando esquerdo não encontrado.");
            device.ConnectionStatusChanged += OnConnection;
            Stage = "manter ligação";
            gatt = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (gatt is not null) gatt.MaintainConnection = true;
            Stage = "descobrir serviços";
            var result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(20));
            Check(result.Status);
            foreach (var s in result.Services) BluetoothLog.Write($"SERVICE {s.Uuid}");
            var candidates = result.Services.Where(s => s.Uuid == ZwiftProtocol.TrainerService || s.Uuid == ZwiftProtocol.Service)
                .OrderBy(s => s.Uuid == ZwiftProtocol.TrainerService ? 0 : 1).ToArray();
            try
            {
                foreach (var candidate in candidates)
                {
                    Stage = $"características {candidate.Uuid}";
                    var chars = await candidate.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(12));
                    Check(chars.Status);
                    foreach (var c in chars.Characteristics) BluetoothLog.Write($"CHAR {c.Uuid} properties={c.CharacteristicProperties}");
                    var candidateRx = chars.Characteristics.FirstOrDefault(c => c.Uuid == ZwiftProtocol.Rx);
                    var candidateTx = chars.Characteristics.FirstOrDefault(c => c.Uuid == ZwiftProtocol.Tx);
                    var candidateAsync = chars.Characteristics.FirstOrDefault(c => c.Uuid == ZwiftProtocol.Async);
                    if (candidateRx is null || candidateTx is null || candidateAsync is null) continue;
                    service = candidate; rx = candidateRx; tx = candidateTx; notifications = candidateAsync; break;
                }
            }
            finally { foreach (var s in result.Services) if (!ReferenceEquals(s, service)) s.Dispose(); }
            if (service is null) throw new IOException("Serviço FC82/00000001 com RX/TX/Async não encontrado. Consulta o registo de diagnóstico.");
            Stage = "subscrever notificações";
            notifications!.ValueChanged += OnFrame; tx!.ValueChanged += OnFrame;
            Check(await notifications.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask().WaitAsync(TimeSpan.FromSeconds(8)));
            var txMode = tx.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate) ? GattClientCharacteristicConfigurationDescriptorValue.Indicate : GattClientCharacteristicConfigurationDescriptorValue.Notify;
            Check(await tx.WriteClientCharacteristicConfigurationDescriptorAsync(txMode).AsTask().WaitAsync(TimeSpan.FromSeconds(8)));
            connected = true; Stage = "iniciar sessão RideOn";
            report("Bluetooth ligado · a iniciar sessão do par…");
            // FF0400 resumes an authorized session; it does not answer an authorization challenge.
            await Write("RideOn"u8.ToArray()); await Task.Delay(500); await Write([0xff, 4, 0]);
            if (device.ConnectionStatus != BluetoothConnectionStatus.Connected) throw new IOException("O Click 2 perdeu a ligação durante a ativação.");
            Stage = "aguardar botões";
            if (!receivingButtons) report("Bluetooth ligado · prime − e + para validar os dois lados.");
            _ = KeepAlive();
        }
        async Task Write(byte[] bytes)
        {
            if (disposed || rx is null) throw new IOException("Click 2 desligado.");
            using var writer = new DataWriter(); writer.WriteBytes(bytes);
            var option = rx.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse) ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
            var status = await rx.WriteValueAsync(writer.DetachBuffer(), option).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            BluetoothLog.Frame($"TX {status}", bytes); Check(status);
        }
        void OnFrame(GattCharacteristic sender, GattValueChangedEventArgs e)
        {
            if (disposed) return;
            var bytes = Read(e.CharacteristicValue); BluetoothLog.Frame($"RX {sender.Uuid}", bytes);
            switch (ZwiftProtocol.ControllerReply(bytes))
            {
                case ClickReply.Buttons:
                    receivingButtons = true;
                    var delta = buttons.UpdatePair(bytes);
                    if (delta != 0) { observed(delta < 0); shift(delta); report("Botões recebidos pela ligação agregada · " + (delta < 0 ? "−" : "+")); }
                    break;
                case ClickReply.VendorChallenge:
                    challengeSeen = true;
                    if (!receivingButtons) report("Bluetooth ligado · pedido de autorização FF03 recebido. Se os botões não responderem, autoriza o Click 2 na app Zwift e fecha-a completamente antes de voltar aqui.");
                    break;
                case ClickReply.RideOn:
                    if (!receivingButtons && !challengeSeen) report("Sessão RideOn respondeu · aguarda validação dos botões − e +");
                    break;
                case ClickReply.LostControl:
                    connected = false; cancellation.Cancel(); report("Click 2 retirou o controlo da sessão. Fecha as outras apps e volta a ligar.");
                    break;
            }
        }
        void OnConnection(BluetoothLEDevice sender, object args)
        {
            BluetoothLog.Write($"LINK {sender.ConnectionStatus}");
            if (!disposed && sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            {
                connected = false; cancellation.Cancel();
                report("Ligação Click 2 perdida. Acorda o esquerdo e volta a ligar. Consulta o registo para ver a etapa e as respostas recebidas.");
            }
        }
        async Task KeepAlive()
        {
            try
            {
                while (!cancellation.IsCancellationRequested && connected)
                { await Task.Delay(10000, cancellation.Token); await Write("RideOn"u8.ToArray()); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!disposed) { connected = false; BluetoothLog.Write($"KEEPALIVE {ex}"); report($"Click 2 interrompido: {ex.Message}"); } }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; connected = false; cancellation.Cancel(); BluetoothLog.Write("SESSION disposed");
            if (tx is not null) tx.ValueChanged -= OnFrame;
            if (notifications is not null) notifications.ValueChanged -= OnFrame;
            if (device is not null) device.ConnectionStatusChanged -= OnConnection;
            gatt?.Dispose(); service?.Dispose(); device?.Dispose();
        }
    }
}
