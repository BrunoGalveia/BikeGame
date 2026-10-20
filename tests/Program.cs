using BikeGame;

int passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; Console.WriteLine($"PASS {name}"); }
void Reject(Action action, string name) { try { action(); } catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or ArgumentOutOfRangeException) { passed++; Console.WriteLine($"PASS {name}"); return; } throw new Exception(name); }
const string json = """
{"version":1,"name":"Teste","segments":[{"name":"Plano","distanceMeters":1000,"gradePercent":0},{"name":"Subida","distanceMeters":1000,"gradePercent":5}]}
""";
var route = Route.Parse(json);
Check(route.TotalMeters == 2000, "Total do percurso");
Check(route.SegmentAt(999.9) == 0 && route.SegmentAt(1000) == 1, "Transição exata de segmento");
var ride = new Ride(route) { Running = true };
ride.Advance(36, 100);
Check(ride.Meters == 1000 && ride.Percent == 50 && ride.SegmentIndex == 1, "Distância e progresso");
ride.Running = false; ride.Advance(36, 10);
Check(ride.Meters == 1000, "Pausa não acumula distância");
ride.Running = true; ride.Advance(36, 1000);
Check(ride.Complete && ride.Percent == 100 && !ride.Running && ride.Meters == 2000, "Conclusão limitada a 100%");
Reject(() => Route.Parse(json.Replace("1000", "0")), "Rejeita distância zero");
Reject(() => Route.Parse(json.Replace("gradePercent\":5", "gradePercent\":17")), "Rejeita inclinação fora do limite");
Reject(() => Route.Parse(json.Replace("version\":1", "version\":2")), "Rejeita versão desconhecida");
Reject(() => Route.Parse(json.Replace("gradePercent", "inclination")), "Rejeita campos desconhecidos");
Reject(() => Route.Parse(json.Replace("distanceMeters\":1000,", "")), "Rejeita distância em falta");
// flags: cadence, total distance, resistance, instantaneous power, energy, HR.
byte[] packet = [0x74, 0x03, 0xB8, 0x0B, 0xA8, 0x00, 0x10, 0, 0, 0, 0, 0xFA, 0, 1, 0, 2, 0, 3, 145];
var t = Ftms.ParseBikeData(packet);
Check(t.SpeedKph == 30 && t.CadenceRpm == 84 && t.PowerWatts == 250 && t.HeartRate == 145, "FTMS com campos opcionais e offsets");
var split = Ftms.ParseBikeData([0x41, 0, 0xEC, 0xFF]);
Check(split.SpeedKph is null && split.PowerWatts == -20, "FTMS More Data e potência com sinal");
Reject(() => Ftms.ParseBikeData([0x40, 0, 0]), "Rejeita pacote truncado");
Check(Ftms.Simulation(-2).SequenceEqual(new byte[] { 0x11, 0, 0, 0x38, 0xFF, 40, 51 }), "Codifica inclinação negativa");
Check(Ftms.Power(250).SequenceEqual(new byte[] { 5, 250, 0 }), "Codifica ERG");
Reject(() => Ftms.Simulation(double.NaN), "Rejeita inclinação NaN");
Reject(() => Ftms.Power(1801), "Rejeita potência acima do limite");
Check(ZwiftProtocol.Gear(1, 0).SequenceEqual(new byte[] { 0x3f, 8, 1, 16, 0, 0x22, 3, 0x38, 0xcc, 0x3a }), "Mudança 1: protobuf e relação 7500");
Check(ZwiftProtocol.Gear(8, 128).SequenceEqual(new byte[] { 0x3f, 8, 1, 16, 0x80, 1, 0x22, 4, 0x38, 0xa0, 0x83, 1 }), "Mudança 8 e sequência multibyte");
Check(ZwiftProtocol.Ratio(24) == 54899, "Mudança 24: relação máxima");
Check(ZwiftProtocol.Gear(0, 1).SequenceEqual(new byte[] { 0x3f, 8, 1, 16, 1, 0x22, 2, 0x38, 0 }), "Desativa mudanças antes de ERG/Stop");
Reject(() => ZwiftProtocol.Gear(25, 0), "Rejeita mudança 25");
Check(ZwiftProtocol.TryButtons([0x23, 0x10, 1, 8, 0xff, 0x23], out var bitmap) && bitmap == 0x11ff, "Click 2: bitmap com campos adicionais");
Check(!ZwiftProtocol.TryButtons([0x23, 8, 0xff], out _), "Click 2: varint truncado");
Check(!ZwiftProtocol.TryButtons([0x23, 8, 0xff, 0xff, 0xff, 0xff, 0x1f], out _), "Click 2: overflow no bitmap");
Check(!ZwiftProtocol.TryButtons([0x23, 0x12, 100, 8], out _), "Click 2: tamanho inválido");
var left = new ClickButtonState();
Check(left.Update([0x23, 8, 0xff, 0x21], true) == -1, "Click 2 esquerdo: menos é uma mudança mais leve");
Check(left.Update([0x23, 8, 0xff, 0x21], true) == 0, "Click 2: botão mantido não repete mudança");
left.Update([0x23, 8, 0xff, 0x23], true);
Check(left.Update([0x23, 8, 0xff, 0x21], true) == -1, "Click 2: soltar e premir volta a mudar");
var right = new ClickButtonState();
Check(right.Update([0x23, 8, 0xff, 3], false) == 1, "Click 2 direito: mais é uma mudança mais pesada");
Check(new ClickButtonState().Update([0x23, 8, 0xff, 0x21], false) == 0, "Click 2: filtra o botão do outro lado");
Console.WriteLine($"Total: {passed} testes passaram.");
// Captured from the user's Click 2 firmware 1.1.0 over the single FC82 left endpoint.
var pair = new ClickButtonState();
var plusPacket = Convert.FromHexString("2308FFDFFFFF0F");
var minusPacket = Convert.FromHexString("2308FFFDFFFF0F");
var releasedPacket = Convert.FromHexString("2308FFFFFFFF0F");
Check(pair.UpdatePair(plusPacket) == 1, "Captura real: + recebido através do esquerdo");
Check(pair.UpdatePair(plusPacket) == 0, "Captura real: repetição não duplica mudança");
Check(pair.UpdatePair(releasedPacket) == 0, "Captura real: libertação não muda");
Check(pair.UpdatePair(minusPacket) == -1, "Captura real: − na mesma sessão do +");
Check(pair.UpdatePair(minusPacket) == 0, "Captura real: − mantido não repete");
Check(ZwiftProtocol.ControllerReply(Convert.FromHexString("FF0500FA05180A0C")) == ClickReply.Other, "FF05 de estado não é bloqueio");
Check(ZwiftProtocol.ControllerReply(Convert.FromHexString("FF03000A2102")) == ClickReply.VendorChallenge, "FF03 é pedido de autorização, não fecha ligação");
Check(ZwiftProtocol.ControllerReply("RideOn"u8.ToArray()) == ClickReply.RideOn, "Resposta RideOn sem sufixo é válida");
Check(ZwiftProtocol.ControllerReply(plusPacket) == ClickReply.Buttons, "Pacote real classificado como botões");
Check(ZwiftProtocol.ControllerReply([0xff]) == ClickReply.Other, "FF isolado não inventa bloqueio");
Check(ZwiftProtocol.ControllerReply([0xfe]) == ClickReply.LostControl, "Perda explícita de controlo identificada");
Console.WriteLine($"Total final: {passed} testes passaram.");
