# BikeGame / KICKR Studio

App nativa Windows em C#/.NET 8 e WPF. Bluetooth Low Energy direto pelas APIs do Windows, usando Fitness Machine Service (FTMS). Sem servidor, conta ou subscrição.

## Abrir

Abre `outputs/BikeGame-win-x64/BikeGame.exe`. Requer Windows 10 2004 ou mais recente, Bluetooth LE e .NET 8 Desktop Runtime x64. Mantém todos os ficheiros da pasta junto do executável.

1. Liga o KICKR à corrente e ativa o Bluetooth no PC. Fecha o controlo do rolo no Zwift/Wahoo/outras apps.
2. Escolhe **Procurar Bluetooth**, aguarda 10 segundos, seleciona o KICKR e clica **Ligar**.
3. Pedala para receber velocidade, potência e cadência. A frequência cardíaca só aparece se vier incluída nos dados FTMS do rolo.
4. Abre ou edita um percurso e clica **Aplicar JSON**. **Iniciar / retomar** aplica a inclinação do segmento atual.
5. **Pausar** guarda o progresso em memória e envia Stop FTMS. **Parar controlo** também interrompe o controlo; não trava fisicamente o volante.

**Demonstração** permite experimentar o painel e as transições sem dispositivo, com dados claramente simulados. Não liga ao hardware.

## Percursos JSON

```json
{
  "version": 1,
  "name": "Subida de 1 km",
  "segments": [
    { "name": "Subida", "distanceMeters": 1000, "gradePercent": 4 }
  ]
}
```

Todos os campos são obrigatórios. Distâncias em metros positivos (até 1 000 000 por segmento), inclinação de -16 a +16%, até 10000 segmentos. Campos desconhecidos são rejeitados para detetar erros de escrita. Um exemplo de 5 km está em `routes/exemplo.json`.

A app integra velocidade FTMS × tempo para calcular distância e percentagem. A distância não é a distância física percorrida na estrada e pode diferir do Zwift: não há modelo de velocidade por potência, peso ou aerodinâmica. O gráfico representa a elevação relativa acumulada, começando em zero. Pausar, perder ligação ou ficar 3 segundos sem velocidade recente interrompe o percurso. Retomar exige dados recentes e reaplica a inclinação atual. Ao concluir, envia Stop FTMS. Reiniciar ou aplicar outro JSON repõe o progresso. O progresso não é persistido quando fechas a app.

O controlo manual pausa a progressão e aplica inclinação (SIM) ou potência alvo (ERG, 0–1800 W). Os modos são alternativos: retomar o percurso volta a SIM. As capacidades anunciadas pelo rolo determinam os botões disponíveis; os comandos exigem resposta positiva FTMS. O firmware pode impor limites adicionais, reportados no painel. Em caso de erro de comunicação, a app tenta parar e encerra a ligação; se a ligação já tiver caído, não pode garantir que o rolo recebeu a paragem.

## Zwift Cog / Click 2 e mudanças virtuais

A app agora envia as **24 mudanças virtuais reais** ao serviço Bluetooth FC82 do KICKR, através do protocolo RideOn e relações de transmissão. Não aproxima as mudanças alterando a inclinação. Esta integração é experimental e ainda precisa de validação no teu rolo/firmware. A app não está certificada pela Wahoo ou Zwift.

1. Liga o rolo normalmente. O painel indica se o serviço de mudanças foi encontrado.
2. Clica **Ativar mudanças (SIM)**. Começa na mudança 8; se estiveres fora de SIM, este botão aplica primeiro inclinação 0%. Durante um percurso mantém a inclinação atual.
3. Experimenta **− Mais leve** / **+ Mais pesada**. O valor só muda depois de a escrita Bluetooth ter sucesso. O número é a seleção desta app, não uma leitura de confirmação da relação mecânica ou de outra sessão Zwift.
4. Para usar os comandos físicos, acorda o **lado esquerdo do Zwift Click 2**, clica **Procurar Click 2**, seleciona o único par e clica **Ligar Click 2**. Acorda depois o direito: os seus botões chegam pela ligação ao esquerdo, como no modo agregado do Zwift. Não há uma segunda seleção ou ligação ao comando direito. O painel confirma separadamente a receção de − e +.
5. O botão **−** do esquerdo torna a mudança mais leve; o **+** do direito torna-a mais pesada. Uma pressão corresponde a uma mudança; manter premido não repete. Os botões funcionam enquanto SIM e as mudanças estiverem ativos. A inclinação muda normalmente entre segmentos, mantendo a mudança selecionada.

**Autorização do Click 2:** alguns firmwares exigem autorização na app Zwift, que pode expirar. A app distingue ligação BLE, resposta RideOn e botões realmente recebidos. Uma mensagem de fabricante FF05 é estado normal e não fecha a ligação. Uma mensagem FF03 indica o fluxo de autorização, mas não é tratada como prova de recusa: a app continua a observar os botões. Se estes não responderem ou a sessão terminar, liga o par na app Zwift principal (não apenas no ecrã Equipamento do Companion), confirma os botões, fecha o Zwift completamente e volta a ligar na BikeGame. A BikeGame não implementa a autorização proprietária. Ver [notas do fluxo agregado](https://github.com/OpenBikeControl/bikecontrol/blob/main/lib/bluetooth/devices/zwift/zwift_unlock.dart).

A ligação envia uma sondagem RideOn a cada 10 segundos; isso verifica a sessão e não substitui autorização nem garante que um comando nunca adormece. Se o par perder ligação, acorda o esquerdo e usa **Ligar Click 2** novamente. **Guardar diagnóstico…** exporta o registo de anúncios, tipo de endereço, serviços, características, mensagens e erros. Os registos são locais em `logs/` junto do executável. Pausar ou parar suspende as mudanças; retomar o percurso reaplica a seleção. ERG desativa a relação virtual no rolo: volta a SIM para usar mudanças.

Validação física em 20/10/2026: Click 2 firmware 1.1.0, endereço BLE aleatório, serviço FC82, RX WriteWithoutResponse, TX Indicate, Async Notify. Recebidos os pacotes reais `2308FFFDFFFF0F` (−), `2308FFDFFFFF0F` (+) e `2308FFFFFFFF0F` (libertação), todos pela ligação ao esquerdo. Estes pacotes fazem parte dos testes de regressão. A ligação e os botões foram observados fisicamente; isso não confirma a resistência de todas as mudanças no KICKR.

O Click 1, Zwift Play e Zwift Ride não estão integrados. Fecha o controlo do Zwift/Wahoo antes de usar a BikeGame; a app não sincroniza mudanças com uma sessão Zwift em paralelo. Referências do protocolo e atribuições em `THIRD_PARTY.md`.

Velocidade, cadência, potência e frequência cardíaca são mostradas apenas quando recebidas e recentes; `—` significa ausente. Pacotes FTMS inválidos são ignorados. Não há fallback ANT+, Wi-Fi/Direct Connect ou protocolos Wahoo proprietários nesta versão.

## Desenvolvimento e verificação

```powershell
dotnet build BikeGame.csproj
dotnet run --project tests/CoreTests.csproj
dotnet publish BikeGame.csproj -c Release -r win-x64 --self-contained false -o outputs/BikeGame-win-x64
```

Teste do arranque WPF, demonstração, transição de segmento e captura do painel:

```powershell
outputs/BikeGame-win-x64/BikeGame.exe --smoke-test C:\dev\BikeGame\outputs\smoke.png
```

Serviço 0x1826; dados Indoor Bike 0x2AD2; capacidades 0x2ACC; controlo 0x2AD9; estado 0x2ADA. Comandos: Request Control 0x00, Start 0x07, Stop 0x08, Set Target Power 0x05 e Set Indoor Bike Simulation Parameters 0x11. Pedidos serializados e confirmações por indications, com timeout. SIM usa vento 0, Crr 0,004 e Cw 0,51. Mudanças: serviço 0xFC82, característica RX `00000003-19ca-4651-86e5-fa29dcdd09d1`, handshake `RideOn 02 03`, comando 0x3F com protobuf (tipo campo 1, sequência campo 2 e payload campo 4; relação no campo 7 do payload). Comandos Click 2: serviço `00000001-19ca-4651-86e5-fa29dcdd09d1`, Async 00000002, RX 00000003 e TX 00000004 com o mesmo sufixo. Notificações 0x23, bitmap protobuf campo 1, bits ativos a zero; − máscara 0x100, + máscara 0x1000.

Fontes: [Bluetooth SIG FTMS](https://www.bluetooth.com/specifications/specs/fitness-machine-service-1-0/), [Microsoft GATT Client](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client), [Wahoo CORE 2](https://eu.wahoofitness.com/devices/indoor-cycling/bike-trainers/kickr-core-2-zwift-cog-and-click-buy).

Testes de software não confirmam compatibilidade física: é necessário validar descoberta, notificações e controlo no teu KICKR CORE 2 e firmware.
