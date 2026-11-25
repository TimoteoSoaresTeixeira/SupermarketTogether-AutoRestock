# AutoRestock: Supermarket Together

Plugin BepInEx 5 para **Supermarket Together**. Ele compra caixas sozinho quando um produto fica
**sem caixas na loja e sem nada no estoque** por um tempo seguido (padrão: **20 segundos**).

## Como funciona

A cada intervalo de verificação (padrão: 1 segundo), o mod olha cada produto desbloqueado:

1. O produto precisa ter **etiqueta em alguma prateleira ou no estoque**. Assim o mod não compra produto que você não vende.
2. Ele soma as unidades **no estoque** e **em caixas**: no chão, na mão de funcionários e na mão de jogadores. Também entram as caixas já pagas que ainda vão chegar.
3. Se essa soma ficar **no limite de unidades ou abaixo dele** (padrão: 0, ou seja, zerado) pelo tempo configurado, ele compra `CaixasPorPedido` caixas. O preço é o mesmo do tablet e o dinheiro sai do saldo da loja.
4. Ele respeita um intervalo mínimo entre compras do mesmo produto, uma reserva mínima de dinheiro e um gasto máximo por dia.

## Aba "Reposição" no Quadro de Gestão

O mod adiciona uma aba **Reposição** à barra de botões do **Quadro de Gestão**, ao lado de "Funcionários", "Estatísticas" e "Faturas". Ela usa o mesmo visual das outras abas do jogo. Nela você pode:

- ligar e desligar o mod (a tecla **F9** também faz isso, em qualquer lugar);
- ajustar o tempo, o limite de unidades, as caixas por pedido, o dinheiro e o intervalo de verificação com os botões **-** e **+** (segure para mudar rápido);
- ver o status, quanto foi gasto no dia, quais produtos estão prestes a ser comprados e as últimas compras.

As mudanças são salvas no `.cfg` quando você sai da aba ou fecha o quadro.

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
| `SegundosSemEstoque` | `20` | Segundos seguidos abaixo do limite antes de comprar |
| `IntervaloDeVerificacao` | `1` | De quantos em quantos segundos o estoque é conferido |
| `LimiteDeUnidades` | `0` | Compra quando estoque + caixas ficar nesse valor ou abaixo (0 = zerado) |
| `CaixasPorPedido` | `1` | Caixas compradas por vez |
| `ReservaMinimaDeDinheiro` | `0` | Não compra se o saldo ficar abaixo disso |
| `GastoMaximoPorDia` | `0` | Limite de gasto automático por dia do jogo (0 = sem limite) |
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
