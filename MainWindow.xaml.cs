using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BikeGame;

public partial class MainWindow : Window
{
    readonly Trainer trainer = new();
    readonly Click2 click2 = new();
    readonly Queue<int> shifts = new();
    int selectedGear = 8;
    bool gearsEnabled, simActive;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Dictionary<string, double> timestamps = new();
    Ride ride = null!;
    Telemetry telemetry = new();
    double previousTick;
    bool demo, busy, ticking, closing;
    double demoGrade;
    int? demoPower;
    int appliedSegment = -1;
    public MainWindow()
    {
        InitializeComponent();
        JsonEditor.Text = File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "routes", "exemplo.json"));
        LoadRoute();
        trainer.Discovered += d => Dispatcher.BeginInvoke(() =>
        {
            if (Devices.Items.Cast<TrainerDevice>().All(x => x.Address != d.Address)) Devices.Items.Add(d);
            if (Devices.SelectedIndex < 0) Devices.SelectedIndex = 0;
        });
        trainer.Measurement += t => Dispatcher.BeginInvoke(() => Accept(t));
        click2.Discovered += d => Dispatcher.BeginInvoke(() =>
        {
            var list = ClickDevices;
            if (list.Items.Cast<ClickDevice>().All(x => x.Address != d.Address)) list.Items.Add(d);
            if (list.SelectedIndex < 0) list.SelectedIndex = 0;
        });
        click2.Status += message => Dispatcher.BeginInvoke(() =>
        {
            ClickStatusLabel.Text = message;
        });
        click2.ButtonObserved += left => Dispatcher.BeginInvoke(() =>
        {
            (left ? LeftClickLabel : RightClickLabel).Text = (left ? "Botão −" : "Botão +") + " recebido · " + DateTime.Now.ToString("HH:mm:ss");
        });
        click2.Shift += delta => Dispatcher.BeginInvoke(() =>
        {
            if (closing) return;
            if (!gearsEnabled || !simActive) { GearStatusLabel.Text = "Botão recebido · ativa as mudanças em SIM para mudar."; return; }
            if (shifts.Count < 32) shifts.Enqueue(delta);
        });
        trainer.Lost += message => Dispatcher.BeginInvoke(async () =>
        {
            if (demo || closing) return;
            ride.Running = false;
            simActive = false; shifts.Clear();
            try { await StopControl(); } catch { }
            trainer.Dispose(); telemetry = new(); timestamps.Clear();
            ConnectionLabel.Text = "●  Ligação interrompida"; ControlLabel.Text = "Controlo interrompido";
            StatusLabel.Text = message; Refresh();
        });
        timer.Tick += async (_, _) => await Tick(); timer.Start(); Refresh();
    }
    void Accept(Telemetry t)
    {
        var now = clock.Elapsed.TotalSeconds;
        if (t.SpeedKph.HasValue) timestamps["speed"] = now;
        if (t.CadenceRpm.HasValue) timestamps["cadence"] = now;
        if (t.PowerWatts.HasValue) timestamps["power"] = now;
        if (t.HeartRate.HasValue) timestamps["heart"] = now;
        telemetry = new(t.SpeedKph ?? telemetry.SpeedKph, t.CadenceRpm ?? telemetry.CadenceRpm, t.PowerWatts ?? telemetry.PowerWatts, t.HeartRate ?? telemetry.HeartRate);
    }
    bool Fresh(string field) => timestamps.TryGetValue(field, out var time) && clock.Elapsed.TotalSeconds - time < 3;
    async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true; Refresh();
        try { await action(); }
        catch (Exception ex)
        {
            ride.Running = false; StatusLabel.Text = ex.Message;
            if (ex is IOException or TimeoutException)
            {
                try { await StopControl(); } catch { }
                trainer.Dispose(); demo = false; timestamps.Clear(); telemetry = new();
                simActive = false; shifts.Clear();
                ConnectionLabel.Text = "●  Sem ligação"; ControlLabel.Text = "Controlo interrompido; verifica o rolo";
            }
        }
        finally { busy = false; Refresh(); }
    }
    async Task Tick()
    {
        if (ticking || closing) return;
        ticking = true;
        try
        {
            var now = clock.Elapsed.TotalSeconds;
            var dt = now - previousTick; previousTick = now;
            if (demo)
            {
                double wave = Math.Sin(now / 4);
                Accept(new(Math.Max(6, 28 - demoGrade * 1.4 + wave), 84 + wave * 3, demoPower ?? (int)(175 + demoGrade * 9 + wave * 12 + (gearsEnabled ? (selectedGear - 8) * 8 : 0))));
            }
            if (!busy && ride.Running)
            {
                if (!Fresh("speed") || dt > 3)
                {
                    ride.Running = false;
                    await Run(async () => { await StopControl(); StatusLabel.Text = "Percurso pausado: sem velocidade recente ou computador suspenso. Retoma quando os dados regressarem."; ControlLabel.Text = "Controlo parado"; });
                }
                else
                {
                    ride.Advance(telemetry.SpeedKph ?? 0, dt);
                    if (ride.Complete)
                        await Run(async () => { await StopControl(); ControlLabel.Text = "Controlo parado"; StatusLabel.Text = "Percurso concluído. Bom treino!"; });
                    else if (ride.SegmentIndex != appliedSegment) await Run(ApplySegment);
                }
            }
            if (!busy && simActive && gearsEnabled && shifts.Count > 0)
            {
                int delta = shifts.Dequeue(); await Run(() => ChangeGear(delta));
            }
            Refresh();
        }
        finally { ticking = false; }
    }
    async Task ApplySegment()
    {
        int index = ride.SegmentIndex;
        var segment = ride.Route.Segments[index];
        await SetGrade(segment.GradePercent);
        appliedSegment = index;
        StatusLabel.Text = $"Percurso ativo · {segment.Name}";
    }
    async Task SetGrade(double grade)
    {
        if (demo) { demoGrade = grade; demoPower = null; }
        else await trainer.SetGrade(grade);
        simActive = true;
        if (gearsEnabled && !demo && trainer.CurrentGear != selectedGear) await trainer.SetGear(selectedGear);
        if (gearsEnabled) GearStatusLabel.Text = $"Mudança {selectedGear} / 24 · " + (demo ? "simulada" : "enviada ao KICKR");
        ControlLabel.Text = $"SIM · {grade:+0.0;-0.0;0.0}% confirmados" + (demo ? " (demonstração)" : "");
    }
    async Task StopControl()
    {
        simActive = false; shifts.Clear();
        if (gearsEnabled) GearStatusLabel.Text = "Mudanças pausadas · retoma SIM para continuar";
        if (!demo) await trainer.Stop();
        else { demoGrade = 0; demoPower = null; }
    }
    async Task ChangeGear(int delta)
    {
        if (!gearsEnabled || !simActive) throw new InvalidOperationException("Ativa as mudanças em modo SIM primeiro.");
        int next = Math.Clamp(selectedGear + delta, 1, 24);
        if (next == selectedGear) return;
        if (!demo) await trainer.SetGear(next);
        selectedGear = next;
        GearStatusLabel.Text = $"Mudança {next} / 24 · " + (demo ? "simulada" : "enviada ao KICKR");
    }
    async void EnableGearsClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (!demo && !trainer.SupportsVirtualShifting) throw new InvalidOperationException(trainer.VirtualShiftInfo);
        if (!simActive) await SetGrade(ride.Running ? ride.Route.Segments[ride.SegmentIndex].GradePercent : 0);
        if (!demo) await trainer.SetGear(selectedGear);
        gearsEnabled = true;
        GearStatusLabel.Text = $"Mudança {selectedGear} / 24 · " + (demo ? "simulada" : "enviada ao KICKR");
    });
    async void GearDownClick(object sender, RoutedEventArgs e) => await Run(() => ChangeGear(-1));
    async void GearUpClick(object sender, RoutedEventArgs e) => await Run(() => ChangeGear(1));
    async void ScanControllersClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        ClickDevices.Items.Clear(); click2.Scan();
        StatusLabel.Text = "A procurar o par Click 2 durante 10 segundos. Acorda o comando esquerdo.";
        try { await Task.Delay(10000); }
        finally { click2.StopScan(); }
        StatusLabel.Text = $"Pares Click 2 encontrados: {ClickDevices.Items.Count}. Seleciona o par e usa Ligar Click 2.";
    });
    async void ConnectControllersClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (ClickDevices.SelectedItem is not ClickDevice selected) throw new InvalidOperationException("Procura e seleciona o par Click 2 primeiro.");
        LeftClickLabel.Text = "Botão −: por validar"; RightClickLabel.Text = "Botão +: por validar";
        bool connected = await click2.Connect(selected);
        StatusLabel.Text = connected ? "Sessão Bluetooth aberta. Prime − e + para validar o par; ativa as mudanças para controlar o rolo." : "Não foi possível abrir a sessão Click 2. Consulta a mensagem e guarda o diagnóstico.";
    });
    void DisconnectControllersClick(object sender, RoutedEventArgs e)
    {
        click2.Dispose(); shifts.Clear(); ClickStatusLabel.Text = "Click 2: sem ligação"; LeftClickLabel.Text = "Botão −: por validar"; RightClickLabel.Text = "Botão +: por validar";
    }
    void SaveBluetoothLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            BluetoothLog.Write("Diagnostic export requested");
            var dialog = new SaveFileDialog { Filter = "Registo Bluetooth|*.log", FileName = "click2-diagnostico.log" };
            if (dialog.ShowDialog() == true) File.Copy(BluetoothLog.FilePath, dialog.FileName, true);
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
    async void ScanClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        Devices.Items.Clear(); trainer.Scan(); StatusLabel.Text = "A procurar rolos FTMS durante 10 segundos…";
        await Task.Delay(10000); trainer.StopScan(); StatusLabel.Text = $"Pesquisa terminada: {Devices.Items.Count} dispositivo(s). Seleciona o KICKR e liga.";
    });
    async void ConnectClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (Devices.SelectedItem is not TrainerDevice selected) throw new FormatException("Procura e seleciona primeiro o KICKR.");
        demo = false; ride.Running = false; telemetry = new(); timestamps.Clear();
        await StopControl(); trainer.Dispose();
        StatusLabel.Text = "A ligar e a ler as capacidades FTMS…";
        await trainer.Connect(selected.Address);
        gearsEnabled = false; simActive = false;
        GearStatusLabel.Text = trainer.VirtualShiftInfo;
        ConnectionLabel.Text = $"●  {selected.Name}";
        StatusLabel.Text = $"Bluetooth ligado · SIM: {(trainer.SupportsSimulation ? "sim" : "não")} · ERG: {(trainer.SupportsPower ? "sim" : "não")} · leitura ativa.";
    });
    async void DemoClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        await StopControl(); trainer.Dispose(); demo = true; ride.Running = false; appliedSegment = -1;
        timestamps.Clear(); telemetry = new(); demoGrade = 0; demoPower = null;
        simActive = false; gearsEnabled = false; GearStatusLabel.Text = "Demonstração · ativa as mudanças para experimentar";
        ConnectionLabel.Text = "●  DEMONSTRAÇÃO · dados simulados";
        ControlLabel.Text = "Controlo simulado"; StatusLabel.Text = "Demonstração ativa. Inicia o percurso ou experimenta os controlos manuais.";
    });
    async void DisconnectClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        ride.Running = false;
        try { await StopControl(); }
        finally { trainer.Dispose(); demo = false; timestamps.Clear(); telemetry = new(); ConnectionLabel.Text = "●  Sem ligação"; ControlLabel.Text = "Controlo inativo"; }
        StatusLabel.Text = "Desligado.";
    });
    async void StartClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (ride.Complete) throw new FormatException("Reinicia o percurso antes de começar novamente.");
        if (!demo && !trainer.Connected) throw new FormatException("Liga o rolo ou ativa a demonstração.");
        if (!Fresh("speed")) throw new FormatException("Aguarda dados de velocidade recentes; pedala para acordar o rolo.");
        await ApplySegment(); previousTick = clock.Elapsed.TotalSeconds; ride.Running = true;
    });
    async void PauseClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        ride.Running = false; await StopControl(); ControlLabel.Text = "Controlo parado"; StatusLabel.Text = "Percurso pausado. Podes retomar a partir desta distância.";
    });
    async void StopClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        ride.Running = false; await StopControl();
        ControlLabel.Text = "Controlo parado"; StatusLabel.Text = "Comando de paragem enviado. O volante do rolo continua a rodar por inércia.";
    });
    async void ResetClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        ride.Running = false; await StopControl(); ride = new(ride.Route); appliedSegment = -1;
        ControlLabel.Text = "Controlo parado"; StatusLabel.Text = "Percurso reiniciado.";
    });
    void LoadRoute() { var route = Route.Parse(JsonEditor.Text); ride = new(route); appliedSegment = -1; Refresh(); }
    async void ApplyRouteClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var valid = Route.Parse(JsonEditor.Text);
        ride.Running = false; await StopControl(); ride = new(valid); appliedSegment = -1;
        ControlLabel.Text = "Controlo parado"; StatusLabel.Text = "Percurso carregado e progresso reiniciado.";
    });
    void OpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Percurso JSON|*.json" };
        if (dialog.ShowDialog() == true)
            try { JsonEditor.Text = File.ReadAllText(dialog.FileName); StatusLabel.Text = "JSON aberto no editor. Usa Aplicar JSON para carregar o percurso."; } catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
    void SaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Route.Parse(JsonEditor.Text);
            var dialog = new SaveFileDialog { Filter = "Percurso JSON|*.json", FileName = "percurso.json" };
            if (dialog.ShowDialog() == true) { File.WriteAllText(dialog.FileName, JsonEditor.Text); StatusLabel.Text = "Percurso guardado."; }
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
    static double Number(string text) => double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : throw new FormatException("Indica um número válido.");
    async void GradeClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        double grade = Number(GradeInput.Text);
        if (grade is < -16 or > 16) throw new FormatException("Usa uma inclinação entre -16 e 16%.");
        ride.Running = false; await SetGrade(grade); StatusLabel.Text = "Inclinação manual aplicada; percurso pausado.";
    });
    async void PowerClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        double value = Number(PowerInput.Text);
        if (value is < 0 or > 1800 || value != Math.Truncate(value)) throw new FormatException("Usa potência inteira entre 0 e 1800 W.");
        ride.Running = false;
        simActive = false; shifts.Clear();
        if (demo) demoPower = (int)value; else await trainer.SetPower((int)value);
        GearStatusLabel.Text = "ERG ativo · mudanças suspensas";
        ControlLabel.Text = $"ERG · {value:0} W confirmados" + (demo ? " (demonstração)" : ""); StatusLabel.Text = "Potência manual aplicada; percurso pausado.";
    });
    void Refresh()
    {
        if (ride is null) return;
        PowerLabel.Text = Fresh("power") ? $"{telemetry.PowerWatts} W" : "— W";
        SpeedLabel.Text = Fresh("speed") ? $"{telemetry.SpeedKph:0.0} km/h" : "— km/h";
        CadenceLabel.Text = Fresh("cadence") ? $"{telemetry.CadenceRpm:0} rpm" : "— rpm";
        HeartLabel.Text = Fresh("heart") ? $"{telemetry.HeartRate} bpm" : "— bpm";
        RouteLabel.Text = ride.Route.Name; ProgressLabel.Text = $"{ride.Percent:0.0}%"; Progress.Value = ride.Percent;
        DistanceLabel.Text = $"{ride.Meters / 1000:0.00} / {ride.Route.TotalMeters / 1000:0.00} km · Faltam {(ride.Route.TotalMeters - ride.Meters) / 1000:0.00} km";
        TimeLabel.Text = TimeSpan.FromSeconds(ride.Seconds).ToString(@"hh\:mm\:ss");
        var s = ride.Route.Segments[ride.SegmentIndex];
        SegmentLabel.Text = $"{ride.SegmentIndex + 1}/{ride.Route.Segments.Length} · {s.Name} · {s.GradePercent:+0.0;-0.0;0.0}%";
        bool connected = demo || trainer.Connected;
        GearLabel.Text = gearsEnabled && connected ? (simActive ? $"{selectedGear} / 24" : $"{selectedGear} · pausa") : "Inativa";
        GearLabel.ToolTip = !simActive ? "Mudanças pausadas; ativa SIM para retomar." : "Mudança selecionada pela app e enviada ao rolo, sem leitura de confirmação da relação.";
        EnableGearsButton.IsEnabled = !busy && connected && (demo || (trainer.SupportsVirtualShifting && trainer.SupportsSimulation));
        GearDownButton.IsEnabled = !busy && connected && gearsEnabled && simActive && selectedGear > 1;
        GearUpButton.IsEnabled = !busy && connected && gearsEnabled && simActive && selectedGear < 24;
        ScanClickButton.IsEnabled = ConnectClickButton.IsEnabled = !busy && !ride.Running;
        DisconnectClickButton.IsEnabled = !busy;
        ScanButton.IsEnabled = ConnectButton.IsEnabled = DemoButton.IsEnabled = !busy && !ride.Running;
        DisconnectButton.IsEnabled = !busy && connected;
        StartButton.IsEnabled = !busy && connected && !ride.Running && !ride.Complete && (demo || trainer.SupportsSimulation);
        PauseButton.IsEnabled = !busy && ride.Running;
        StopButton.IsEnabled = !busy && connected;
        ResetButton.IsEnabled = ApplyRouteButton.IsEnabled = !busy;
        GradeButton.IsEnabled = !busy && connected && (demo || trainer.SupportsSimulation);
        PowerButton.IsEnabled = !busy && connected && (demo || trainer.SupportsPower);
        DrawProfile();
    }
    void ProfileSizeChanged(object sender, SizeChangedEventArgs e) { if (ride is not null) DrawProfile(); }
    void DrawProfile()
    {
        Profile.Children.Clear(); var width = Profile.ActualWidth; if (width < 1) return;
        var heights = new List<double> { 0 }; double elevation = 0;
        foreach (var segment in ride.Route.Segments) { elevation += segment.DistanceMeters * segment.GradePercent / 100; heights.Add(elevation); }
        double min = heights.Min(), span = Math.Max(10, heights.Max() - min), x = 0;
        double Y(double h) => 110 - (h - min) / span * 90;
        var line = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(87, 222, 193)), StrokeThickness = 3 };
        line.Points.Add(new(0, Y(0))); elevation = 0;
        foreach (var segment in ride.Route.Segments)
        {
            x += segment.DistanceMeters / ride.Route.TotalMeters * width; elevation += segment.DistanceMeters * segment.GradePercent / 100;
            line.Points.Add(new(x, Y(elevation)));
        }
        Profile.Children.Add(line);
        double position = ride.Percent / 100 * width;
        Profile.Children.Add(new Line { X1 = position, X2 = position, Y1 = 5, Y2 = 125, Stroke = Brushes.White, StrokeThickness = 2, StrokeDashArray = [3, 3] });
    }
    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        if (busy) { StatusLabel.Text = "Aguarda o fim da operação antes de fechar."; return; }
        closing = true; busy = true; ride.Running = false; timer.Stop();
        try { await StopControl(); } catch { }
        trainer.Dispose(); click2.Dispose(); Close();
    }
    internal async Task SmokeTest(string screenshotPath)
    {
        demo = true; ConnectionLabel.Text = "●  DEMONSTRAÇÃO · dados simulados";
        Devices.Items.Add(new TrainerDevice(1, "KICKR CORE 2 · demo")); Devices.SelectedIndex = 0;
        ClickDevices.Items.Add(new ClickDevice(1, Windows.Devices.Bluetooth.BluetoothAddressType.Random)); ClickDevices.SelectedIndex = 0;
        Accept(new(28, 85, 195));
        await SetGrade(0); gearsEnabled = true;
        await ChangeGear(1);
        if (selectedGear != 9) throw new Exception("Mudança virtual não avançou.");
        await ChangeGear(-1);
        if (selectedGear != 8) throw new Exception("Mudança virtual não recuou.");
        await ApplySegment(); ride.Running = true;
        ride.Advance(36, 100); await ApplySegment();
        if (ride.SegmentIndex != 1 || demoGrade != 3 || ride.Percent != 20) throw new Exception("Transição SIM no painel falhou.");
        ride.Running = false;
        ride = new(ride.Route); await ApplySegment(); ride.Running = true;
        await Task.Delay(1200); Refresh(); UpdateLayout();
        if (ride.Meters <= 0 || !PowerLabel.Text.Contains("W")) throw new Exception("Demonstração não avançou.");
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(screenshotPath); encoder.Save(stream);
        ClickDevices.IsDropDownOpen = true;
        await Task.Delay(200); UpdateLayout();
        var popup = (System.Windows.Controls.Primitives.Popup)ClickDevices.Template.FindName("PART_Popup", ClickDevices);
        var popupContent = (FrameworkElement)popup.Child;
        popupContent.UpdateLayout();
        var popupBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(popupContent.ActualWidth), (int)Math.Ceiling(popupContent.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        popupBitmap.Render(popupContent);
        var popupEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        popupEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(popupBitmap));
        using var popupStream = File.Create(screenshotPath + ".dropdown.png"); popupEncoder.Save(popupStream);
        ClickDevices.IsDropDownOpen = false;
        ride.Running = false;
    }
}
