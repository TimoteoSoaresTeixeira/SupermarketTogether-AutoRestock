using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace AutoRestock
{
    /// <summary>
    /// Compra caixas automaticamente quando um produto fica sem unidades no estoque (storage)
    /// e sem caixas soltas na loja por um tempo configurável (padrão 20 s).
    /// Só roda no host / single-player, porque a compra é processada pelo servidor.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.autorestock.supermarkettogether";
        public const string PluginName = "AutoRestock";
        public const string PluginVersion = "1.0.0";

        // Índices de ManagerBlackboard.dummyArrayExistences (mesma ordem usada pelo tablet do jogo).
        private const int ShelvesIndex = 0;
        private const int StorageIndex = 1;
        private const int BoxesIndex = 2;

        internal static ManualLogSource Log;

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<KeyboardShortcut> _toggleKey;
        private ConfigEntry<float> _emptySeconds;
        private ConfigEntry<int> _boxesPerOrder;
        private ConfigEntry<float> _minFundsReserve;
        private ConfigEntry<float> _cooldownSeconds;
        private ConfigEntry<bool> _requireEmptyShelves;
        private ConfigEntry<bool> _onlyWhenStoreOpen;

        private static readonly AccessTools.FieldRef<ManagerBlackboard, GameObject[]> ExistencesRef =
            AccessTools.FieldRefAccess<ManagerBlackboard, GameObject[]>("dummyArrayExistences");

        private static readonly Func<ManagerBlackboard, int, float> PricePerBox =
            AccessTools.MethodDelegate<Func<ManagerBlackboard, int, float>>(
                AccessTools.Method(typeof(ManagerBlackboard), "PricePerBoxRetrieve"));

        // productID -> Time.time em que o produto foi visto zerado pela primeira vez (sequência atual).
        private readonly Dictionary<int, float> _emptySince = new Dictionary<int, float>();
        // productID -> Time.time da última compra automática.
        private readonly Dictionary<int, float> _lastPurchase = new Dictionary<int, float>();
        // productID -> Time.time do último aviso de "sem dinheiro" (para não lotar o log).
        private readonly Dictionary<int, float> _lastNoFundsWarning = new Dictionary<int, float>();

        private readonly Dictionary<int, int> _shelves = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _storage = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _boxes = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _pending = new Dictionary<int, int>();
        private readonly HashSet<int> _labeled = new HashSet<int>();

        private float _nextCheck;
        private bool _inputBroken;
        private bool _loggedScanError;

        private void Awake()
        {
            Log = Logger;

            _enabled = Config.Bind("Geral", "Ativado", true,
                "Liga/desliga a compra automática.");
            _toggleKey = Config.Bind("Geral", "TeclaLigaDesliga", new KeyboardShortcut(KeyCode.F9),
                "Tecla para ligar/desligar durante o jogo.");
            _emptySeconds = Config.Bind("Regras", "SegundosSemEstoque", 20f,
                new ConfigDescription("Quantos segundos seguidos o produto precisa ficar sem caixas e sem estoque antes de comprar.",
                    new AcceptableValueRange<float>(1f, 3600f)));
            _boxesPerOrder = Config.Bind("Regras", "CaixasPorPedido", 1,
                new ConfigDescription("Quantas caixas comprar de cada vez.", new AcceptableValueRange<int>(1, 50)));
            _minFundsReserve = Config.Bind("Regras", "ReservaMinimaDeDinheiro", 0f,
                "Não compra se o saldo ficar abaixo deste valor depois da compra.");
            _cooldownSeconds = Config.Bind("Regras", "IntervaloEntreComprasDoMesmoProduto", 30f,
                new ConfigDescription("Tempo mínimo (segundos) entre duas compras automáticas do mesmo produto.",
                    new AcceptableValueRange<float>(0f, 3600f)));
            _requireEmptyShelves = Config.Bind("Regras", "ExigirPrateleiraVazia", false,
                "Se true, só compra quando o produto também estiver zerado nas prateleiras.");
            _onlyWhenStoreOpen = Config.Bind("Regras", "SomenteComLojaAberta", false,
                "Se true, só compra enquanto a loja estiver aberta.");

            Log.LogInfo($"{PluginName} {PluginVersion} carregado. Tecla: {_toggleKey.Value}. Ativado: {_enabled.Value}");
        }

        private void Update()
        {
            HandleToggle();

            if (Time.time < _nextCheck)
                return;
            _nextCheck = Time.time + 1f;

            if (!_enabled.Value)
                return;

            try
            {
                Check();
            }
            catch (Exception e)
            {
                if (!_loggedScanError)
                {
                    _loggedScanError = true;
                    Log.LogError($"Erro na verificação de estoque (só será mostrado uma vez): {e}");
                }
            }
        }

        private void HandleToggle()
        {
            if (_inputBroken)
                return;
            try
            {
                if (_toggleKey.Value.IsDown())
                {
                    _enabled.Value = !_enabled.Value;
                    _emptySince.Clear();
                    Log.LogMessage($"AutoRestock {(_enabled.Value ? "LIGADO" : "DESLIGADO")}");
                }
            }
            catch (InvalidOperationException)
            {
                // Jogo configurado só com o novo Input System: desativa a tecla, a config continua valendo.
                _inputBroken = true;
                Log.LogWarning("Input legado indisponível; use o arquivo de configuração para ligar/desligar.");
            }
        }

        private void Check()
        {
            // A compra é processada pelo servidor: só o host (ou single-player) roda o mod.
            if (!NetworkServer.active)
                return;

            GameData gameData = GameData.Instance;
            ProductListing listing = ProductListing.Instance;
            if (gameData == null || listing == null)
                return;

            ManagerBlackboard blackboard = gameData.GetComponent<ManagerBlackboard>();
            if (blackboard == null)
                return;

            if (_onlyWhenStoreOpen.Value && !gameData.isSupermarketOpen)
            {
                _emptySince.Clear();
                return;
            }

            if (!ScanExistences(blackboard))
                return;

            float now = Time.time;
            foreach (int productId in listing.availableProducts)
            {
                // Só considera produtos que têm etiqueta em alguma prateleira ou estoque,
                // senão o mod compraria todo produto desbloqueado que você nunca vende.
                bool outOfStock = _labeled.Contains(productId)
                                  && Get(_storage, productId) == 0
                                  && Get(_boxes, productId) == 0
                                  && Get(_pending, productId) == 0
                                  && (!_requireEmptyShelves.Value || Get(_shelves, productId) == 0);

                if (!outOfStock)
                {
                    _emptySince.Remove(productId);
                    continue;
                }

                if (!_emptySince.TryGetValue(productId, out float since))
                {
                    _emptySince[productId] = now;
                    continue;
                }

                if (now - since < _emptySeconds.Value)
                    continue;

                if (_lastPurchase.TryGetValue(productId, out float last) && now - last < _cooldownSeconds.Value)
                    continue;

                if (TryBuy(gameData, blackboard, listing, productId, now))
                {
                    _lastPurchase[productId] = now;
                    _emptySince.Remove(productId);
                }
            }
        }

        /// <summary>
        /// Conta, em uma única passada, as unidades de cada produto nas prateleiras, no estoque e em caixas
        /// (no chão, nas mãos de funcionários e de jogadores). Mesma lógica de ManagerBlackboard.GetProductsExistences.
        /// </summary>
        private bool ScanExistences(ManagerBlackboard blackboard)
        {
            _shelves.Clear();
            _storage.Clear();
            _boxes.Clear();
            _pending.Clear();
            _labeled.Clear();

            GameObject[] roots = ExistencesRef(blackboard);
            if (roots == null || roots.Length <= BoxesIndex)
                return false;

            CountContainers(roots[ShelvesIndex], _shelves);
            CountContainers(roots[StorageIndex], _storage);

            if (roots[BoxesIndex] != null)
            {
                foreach (Transform child in roots[BoxesIndex].transform)
                {
                    BoxData box = child.GetComponent<BoxData>();
                    if (box != null && box.numberOfProducts > 0)
                        Add(_boxes, box.productID, box.numberOfProducts);
                }
            }

            if (NPC_Manager.Instance != null && NPC_Manager.Instance.employeeParentOBJ != null)
            {
                foreach (Transform employee in NPC_Manager.Instance.employeeParentOBJ.transform)
                {
                    NPC_Info info = employee.GetComponent<NPC_Info>();
                    if (info != null && info.boxNumberOfProducts > 0)
                        Add(_boxes, info.boxProductID, info.boxNumberOfProducts);
                }
            }

            if (NetworkManager.singleton is CustomNetworkManager manager && manager.GamePlayers != null)
            {
                foreach (PlayerObjectController player in manager.GamePlayers)
                {
                    if (player == null)
                        continue;
                    PlayerSyncCharacter sync = player.GetComponent<PlayerSyncCharacter>();
                    if (sync != null && sync.syncedNumberOfProducts > 0)
                        Add(_boxes, sync.syncedProductID, sync.syncedNumberOfProducts);
                }
            }

            // Caixas já pagas que ainda vão aparecer na área de entrega.
            if (blackboard.idsToSpawn != null)
            {
                foreach (int id in blackboard.idsToSpawn)
                    Add(_pending, id, 1);
            }

            return true;
        }

        private void CountContainers(GameObject root, Dictionary<int, int> counts)
        {
            if (root == null)
                return;

            foreach (Transform child in root.transform)
            {
                Data_Container container = child.GetComponent<Data_Container>();
                int[] slots = container != null ? container.productInfoArray : null;
                if (slots == null)
                    continue;

                for (int i = 0; i + 1 < slots.Length; i += 2)
                {
                    int id = slots[i];
                    if (id < 0)
                        continue;
                    _labeled.Add(id);
                    if (slots[i + 1] > 0)
                        Add(counts, id, slots[i + 1]);
                }
            }
        }

        private bool TryBuy(GameData gameData, ManagerBlackboard blackboard, ProductListing listing, int productId, float now)
        {
            int boxes = _boxesPerOrder.Value;
            float total = PricePerBox(blackboard, productId) * boxes;
            if (total <= 0f)
                return false;

            string name = ProductName(productId);

            if (gameData.gameFunds - total < _minFundsReserve.Value)
            {
                if (!_lastNoFundsWarning.TryGetValue(productId, out float warned) || now - warned > 60f)
                {
                    _lastNoFundsWarning[productId] = now;
                    Log.LogWarning($"Sem dinheiro para repor {name}: precisa ${total:0.00}, saldo ${gameData.gameFunds:0.00}, reserva ${_minFundsReserve.Value:0.00}");
                }
                return false;
            }

            // Mesmo efeito de ManagerBlackboard.BuyCargo, sem mexer na lista de compras do tablet.
            for (int i = 0; i < boxes; i++)
                blackboard.idsToSpawn.Add(productId);
            blackboard.SpawnRemainingCargoFromAutosave();

            gameData.CmdAlterFunds(-total);
            gameData.CmdmoneySpentOnProducts(total);

            Log.LogMessage($"Comprado automaticamente: {boxes}x caixa de {name} (ID {productId}) por ${total:0.00}");
            return true;
        }

        private static string ProductName(int productId)
        {
            try
            {
                if (LocalizationManager.instance != null)
                {
                    string localized = LocalizationManager.instance.GetLocalizationString("product" + productId);
                    if (!string.IsNullOrEmpty(localized))
                        return localized;
                }
            }
            catch
            {
                // Nome é só para o log; ignora falhas de localização.
            }
            return "produto " + productId;
        }

        private static int Get(Dictionary<int, int> counts, int id) =>
            counts.TryGetValue(id, out int value) ? value : 0;

        private static void Add(Dictionary<int, int> counts, int id, int amount) =>
            counts[id] = Get(counts, id) + amount;
    }
}
