# AutoRestock: Supermarket Together

Plugin BepInEx 5 para **Supermarket Together**. Ele compra caixas sozinho quando um produto fica
**sem caixas na loja e sem nada no estoque** por um tempo seguido (padrão: **20 segundos**).

## Como funciona

A cada segundo, o mod olha cada produto desbloqueado:

1. O produto precisa ter **etiqueta em alguma prateleira ou no estoque**. Assim o mod não compra produto que você não vende.
2. Ele conta as unidades **no estoque** e **em caixas**: no chão, na mão de funcionários e na mão de jogadores. Também conta as caixas já pagas que ainda vão chegar.
3. Se os dois ficarem em **0** pelo tempo configurado, ele compra `CaixasPorPedido` caixas. O preço é o mesmo do tablet e o dinheiro sai do saldo da loja.
4. Ele respeita um intervalo mínimo entre compras do mesmo produto e uma reserva mínima de dinheiro.

A compra não mexe na lista de compras do tablet.

> **Multiplayer:** só funciona para quem **hospeda** a partida (ou no single-player), porque é o servidor que processa a compra. Se os outros jogadores também tiverem o mod, não há problema: lá ele fica inativo.

## Instalação

1. Instale o [BepInEx 5 (x64)](https://github.com/BepInEx/BepInEx/releases) na pasta do jogo, ao lado de `Supermarket Together.exe`.
   Outra opção é o [BepInExPack do Thunderstore](https://thunderstore.io/c/supermarket-together/p/BepInEx/BepInExPack/).
2. Abra o jogo uma vez e feche. Isso cria as pastas do BepInEx.
3. Copie `AutoRestock.dll` para `Supermarket Together\BepInEx\plugins\AutoRestock\`.

## Configuração

O arquivo `BepInEx\config\com.autorestock.supermarkettogether.cfg` é criado na primeira vez que o jogo abre com o mod.

| Opção | Padrão | Descrição |
|---|---|---|
| `Ativado` | `true` | Liga/desliga o mod |
| `TeclaLigaDesliga` | `F9` | Tecla para ligar/desligar no jogo |
| `SegundosSemEstoque` | `20` | Segundos seguidos sem caixa e sem estoque antes de comprar |
| `CaixasPorPedido` | `1` | Caixas compradas por vez |
| `ReservaMinimaDeDinheiro` | `0` | Não compra se o saldo ficar abaixo disso |
| `IntervaloEntreComprasDoMesmoProduto` | `30` | Segundos mínimos entre compras do mesmo produto |
| `ExigirPrateleiraVazia` | `false` | Só compra se a prateleira também estiver vazia |
| `SomenteComLojaAberta` | `false` | Só compra com a loja aberta |

As compras aparecem no log `BepInEx\LogOutput.log`. Para ver um console em tempo real, ative `[Logging.Console] Enabled = true` em `BepInEx\config\BepInEx.cfg`.

## Compilar

Requisitos: [.NET SDK 8+](https://dotnet.microsoft.com/download), o jogo instalado e o BepInEx 5 instalado no jogo. O projeto usa as DLLs do jogo e do BepInEx como referência. Elas **não** fazem parte do repositório.

```powershell
cd src
dotnet build -c Release
# outra pasta de jogo:
dotnet build -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Supermarket Together"
```

Depois de compilar, a DLL é copiada sozinha para `BepInEx\plugins\AutoRestock`. Use `-p:DeployToGame=false` para desligar essa cópia.

## Aviso

Este é um mod não oficial, sem vínculo com os desenvolvedores do jogo. Faça backup dos seus saves.

## Licença

[MIT](LICENSE)
