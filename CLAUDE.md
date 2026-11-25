# CLAUDE.md: AutoRestock (Supermarket Together)

Plugin **BepInEx 5** para o jogo **Supermarket Together** (Unity 2023.2, build **Mono**, rede com **Mirror**, UI com **PlayMaker**).
Ele compra caixas sozinho quando um produto etiquetado fica sem estoque e sem caixas na loja por N segundos.
O painel de opções aparece dentro do **Quadro de Gestão** do jogo.

- Idioma: o usuário fala **português (pt-BR)**. Responda e escreva textos de UI, logs e config em pt-BR.
- O repositório vai para o GitHub. **Nunca** faça commit das DLLs do jogo ou do BepInEx (o `.gitignore` já bloqueia `*.dll`).
- Os commits terminam com `Co-Authored-By: Claude ...`. Não dê push sem o usuário pedir. O `gh` CLI não está instalado.

## Estrutura

```
README.md              Documentação para usuários (pt-BR)
LICENSE                MIT
src/AutoRestock.csproj netstandard2.1; referencia DLLs do jogo/BepInEx com Private=false; copia a DLL para BepInEx\plugins\AutoRestock após o build
src/Plugin.cs          BaseUnityPlugin: config, varredura de estoque, lógica de compra, detecção do quadro aberto
src/ConfigWindow.cs    Painel IMGUI (GUILayout.Window) exibido enquanto o Quadro de Gestão está aberto
```

## Ambiente local

- Jogo: `C:\Program Files (x86)\Steam\steamapps\common\Supermarket Together`
  - Código do jogo: `Supermarket Together_Data\Managed\Assembly-CSharp.dll`
  - BepInEx **5.4.23.6** instalado (`winhttp.dll`, `doorstop_config.ini`, `BepInEx\`)
  - Plugin: `BepInEx\plugins\AutoRestock\AutoRestock.dll`
  - Log: `BepInEx\LogOutput.log` (só existe depois de o jogo rodar com o BepInEx)
  - Config: `BepInEx\config\com.autorestock.supermarkettogether.cfg`
- .NET SDK 8 instalado **só para o usuário**, fora do PATH global:
  ```powershell
  $env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH="$env:DOTNET_ROOT;$env:USERPROFILE\.dotnet\tools;$env:PATH"
  ```
- Decompilador: `ilspycmd` 8.2 (dotnet tool global). Ele precisa de `$env:DOTNET_ROLL_FORWARD="Major"`, porque foi feito para o .NET 6:
  ```powershell
  $m="C:\Program Files (x86)\Steam\steamapps\common\Supermarket Together\Supermarket Together_Data\Managed"
  ilspycmd -t ManagerBlackboard -r $m "$m\Assembly-CSharp.dll" > "$env:TEMP\st_decomp\ManagerBlackboard.cs"
  ```
  Há classes já decompiladas em `%TEMP%\st_decomp\` (podem ter sido apagadas).

## Build

```powershell
cd src
dotnet build -c Release                       # compila e copia para o jogo
dotnet build -c Release -p:DeployToGame=false # só compila
dotnet build -c Release -p:GameDir="D:\...\Supermarket Together"
```
Se o jogo estiver aberto com o mod carregado, a DLL fica bloqueada e a cópia falha. Feche o jogo antes.

## Funções do jogo usadas (descobertas decompilando)

**`ManagerBlackboard`** (fica no mesmo GameObject que `GameData.Instance`; é o "Quadro de Gestão"):
- `GameObject[] dummyArrayExistences` (privado): `[0]` prateleiras, `[1]` estoque (storage), `[2]` caixas no chão.
  - Os filhos de `[0]` e `[1]` têm `Data_Container.productInfoArray`, que é um `int[]` em pares `(productID, quantidade)`. O valor **`-1` indica slot sem etiqueta**.
  - Os filhos de `[2]` têm `BoxData` com `productID` e `numberOfProducts`.
- `GetProductsExistences(id)` (privado) devolve `[prateleiras, estoque, caixas]`. Caixas inclui as que estão nas mãos de funcionários (`NPC_Manager.Instance.employeeParentOBJ` → `NPC_Info.boxProductID/boxNumberOfProducts`) e de jogadores (`CustomNetworkManager.GamePlayers` → `PlayerSyncCharacter.syncedProductID/syncedNumberOfProducts`). O plugin replica essa lógica em `ScanExistences`, em uma única passada.
- `BuyCargo()`: confere `GameData.gameFunds`, chama `CmdAddProductToSpawnList(id)` para cada caixa, depois `GameData.CmdAlterFunds(-total)` e `CmdmoneySpentOnProducts(total)`.
- `public List<int> idsToSpawn` guarda as caixas pagas que ainda não apareceram (só existe no servidor). `SpawnRemainingCargoFromAutosave()` inicia o spawner se `isServer`.
- `PricePerBoxRetrieve(int)` (privado) dá o preço da caixa, igual ao do tablet. O plugin chama via `AccessTools.MethodDelegate`.
- `public GameObject tabsOBJ` fica ativo quando o quadro está aberto. É o teste que o plugin usa para mostrar o painel.

**Outros:**
- `ProductListing.Instance.availableProducts` lista os IDs desbloqueados. `productsData[id].maxItemsPerBox`, `.basePricePerUnit` e `.productTier` descrevem cada produto.
- `GameData.Instance`: `gameFunds`, `gameDay`, `isSupermarketOpen`, `timeOfDay`.
- `LocalizationManager.instance.GetLocalizationString("product" + id)` devolve o nome do produto.
- `GameCanvas.Instance.CreateCanvasNotification(hash)` recebe uma chave de localização, não texto livre.
- O PlayMaker global `FsmVariables.GlobalVariables.FindFsmBool("InOptions")` trava a câmera (`CustomCameraController`).
- Input: o jogo usa Rewired, mas o `UnityEngine.Input` legado está disponível. O plugin tem um fallback caso ele lance `InvalidOperationException`.

## Comportamento atual (v1.2.0)

- Só roda com `NetworkServer.active` (host ou single-player). Nos clientes fica inativo.
- Considera apenas produtos com **etiqueta** (ID ≠ -1) em alguma prateleira ou no estoque.
- Compra quando `estoque + caixas + caixasPendentes*maxItemsPerBox <= LimiteDeUnidades` por `SegundosSemEstoque` seguidos. Também respeita o cooldown por produto, `ReservaMinimaDeDinheiro` e `GastoMaximoPorDia` (o gasto do dia zera quando `gameDay` muda).
- A compra não usa a lista de compras do tablet. Ela coloca os IDs em `idsToSpawn`, chama o spawner e depois os Cmds de dinheiro.
- `Config.SaveOnConfigSet = false`. O `.cfg` é salvo quando o quadro fecha, ao ligar/desligar e ao restaurar os padrões.
- Teclas: **F9** liga/desliga. O painel não tem tecla própria, ele segue o quadro.
- Seções do config: `Geral`, `Regras`, `Dinheiro`, `Painel` (PosicaoX/PosicaoY/Minimizado).

## Valores padrão do config

| Seção | Chave | Padrão | Faixa |
|---|---|---|---|
| Geral | `Ativado` | `true` | |
| Geral | `TeclaLigaDesliga` | `F9` | |
| Regras | `SegundosSemEstoque` | `20` | 1–3600 (slider: 1–300) |
| Regras | `IntervaloDeVerificacao` | `1` | 0.25–60 (slider: 0.25–30) |
| Regras | `LimiteDeUnidades` | `0` | 0–1000 (slider: 0–200) |
| Regras | `CaixasPorPedido` | `1` | 1–50 (slider: 1–20) |
| Regras | `IntervaloEntreComprasDoMesmoProduto` | `30` | 0–3600 (slider: 0–600) |
| Regras | `ExigirPrateleiraVazia` | `false` | |
| Regras | `SomenteComLojaAberta` | `false` | |
| Dinheiro | `ReservaMinimaDeDinheiro` | `0` | slider: 0–20000 |
| Dinheiro | `GastoMaximoPorDia` | `0` (sem limite) | slider: 0–50000 |
| Painel | `PosicaoX` / `PosicaoY` | `-1` (posição padrão) | |
| Painel | `Minimizado` | `false` | |

## Histórico de decisões

1. **v1.0.0** (`fbcda7d`): primeira versão. Configuração só pelo `.cfg`; F9 liga e desliga.
2. **v1.1.0** (`693d1e0`): o usuário pediu opções dentro do jogo. Criei uma janela IMGUI que abria com **F10**, liberava o cursor e travava a câmera pela flag PlayMaker `InOptions`. Entraram também `LimiteDeUnidades`, `IntervaloDeVerificacao` e `GastoMaximoPorDia`.
3. **v1.2.0** (`c1f2542`): o usuário pediu o painel **dentro do Quadro de Gestão**. A tecla F10 e o código de cursor/`InOptions` foram **removidos**. O painel agora segue `tabsOBJ` e pode ser minimizado e arrastado. Depois disso também saiu a referência a `PlayMaker.dll` do `.csproj`, que tinha ficado sem uso.
4. Os caracteres especiais (✓ ✗ ● ○ •) foram trocados por ASCII (`[OK]`, `[X]`, `-`), porque a fonte padrão do IMGUI pode não ter esses glifos.
5. Ao instalar a versão mais nova do `ilspycmd`, o SDK 8 deu erro de formato. Por isso foi fixada a versão **8.2.0.7535**.
6. A `LICENSE` (MIT) está no nome do usuário, que é o mesmo do `git config user.name`.

## O que foi instalado na máquina

- **BepInEx 5.4.23.6 x64**, baixado das releases oficiais no GitHub (`BepInEx_win_x64_5.4.23.6.zip`) e copiado para a pasta do jogo.
- **.NET SDK 8.0.428**, instalado via `dotnet-install.ps1` em `%LOCALAPPDATA%\Microsoft\dotnet`, só para o usuário e fora do PATH do sistema.
- **ilspycmd 8.2.0.7535**, instalado como dotnet tool global em `%USERPROFILE%\.dotnet\tools`.

## Status e próximos passos

- [ ] **Nada foi testado dentro do jogo ainda.** Quando o BepInEx foi instalado, o jogo já estava aberto, então é preciso reiniciá-lo. Depois, confira no `LogOutput.log`:
  - `AutoRestock 1.2.0 carregado`
  - `Comprado automaticamente: ...`
  - ou `Erro na verificação de estoque`
- [ ] Confirmar que `tabsOBJ.activeInHierarchy` só é true com o quadro aberto. Se o painel ficar sempre visível ou nunca aparecer, ajuste `IsBoardOpen()`.
- [ ] Confirmar que os Cmds (`CmdAlterFunds`) funcionam chamados pelo host e que as caixas aparecem na área de entrega.
- [ ] Ideia: transformar o painel IMGUI em uma **aba nativa (uGUI)** do Quadro de Gestão. Para isso, primeiro logue a hierarquia de `tabsOBJ` e do Canvas pai em runtime (nomes, `Canvas.renderMode`, botões de aba) e depois clone um botão de aba existente.
- [ ] Publicar no GitHub. O usuário cria o repositório e roda `git remote add origin ...` seguido de `git push -u origin main`.

## Mods parecidos (referência)

- Order & Organize (ddqminik, usa a tecla F8), StockPlus (RB007) e SmartShopper (Thunderstore, comunidade `supermarket-together`).
