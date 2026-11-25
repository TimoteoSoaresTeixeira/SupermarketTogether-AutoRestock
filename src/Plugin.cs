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
    /// Compra caixas automaticamente quando um produto fica sem estoque (storage) e sem caixas na loja
    /// por um tempo configurável. Só roda no host / single-player, porque a compra é processada pelo servidor.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.autorestock.supermarkettogether";
        public const string PluginName = "AutoRestock";
        public const string PluginVersion = "1.3.0";

        // Índices de ManagerBlackboard.dummyArrayExistences (mesma ordem usada pelo tablet do jogo).
        private const int ShelvesIndex = 0;
        private const int StorageIndex = 1;
        private const int BoxesIndex = 2;
        private const int MaxHistory = 6;

        internal static ManualLogSource Log;

        internal ConfigEntry<bool> Enabled;
        internal ConfigEntry<KeyboardShortcut> ToggleKey;
        internal ConfigEntry<float> EmptySeconds;
        internal ConfigEntry<float> CheckInterval;
        internal ConfigEntry<int> StockThreshold;
        internal ConfigEntry<int> BoxesPerOrder;
        internal ConfigEntry<float> MinFundsReserve;
        internal ConfigEntry<float> MaxSpendPerDay;
        internal ConfigEntry<float> CooldownSeconds;
        internal ConfigEntry<bool> RequireEmptyShelves;
        internal ConfigEntry<bool> OnlyWhenStoreOpen;

        private static readonly AccessTools.FieldRef<ManagerBlackboard, GameObject[]> ExistencesRef =
            AccessTools.FieldRefAccess<ManagerBlackboard, GameObject[]>("dummyArrayExistences");

        private static readonly Func<ManagerBlackboard, int, float> PricePerBox =
            AccessTools.MethodDelegate<Func<ManagerBlackboard, int, float>>(
                AccessTools.Method(typeof(ManagerBlackboard), "PricePerBoxRetrieve"));

        // productID -> Time.time em que o produto entrou abaixo do limite (sequência atual).
        internal readonly Dictionary<int, float> EmptySince = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _lastPurchase = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _lastNoFundsWarning = new Dictionary<int, float>();

        private readonly Dictionary<int, int> _shelves = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _storage = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _boxes = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _pendingBoxes = new Dictionary<int, int>();
        private readonly HashSet<int> _labeled = new HashSet<int>();

        internal readonly List<string> History = new List<string>();
        internal float SpentToday;
        internal int BoxesToday;
        internal string Status = "Aguardando partida...";

        private int _currentDay = -1;
        private float _nextCheck;
        private bool _inputBroken;
        private bool _loggedScanError;
        private BoardTab _boardTab;
        private bool _loggedTabError;
        private ManagerBlackboard _blackboard;

        private void Awake()
        {
            Log = Logger;
            // Evita gravar o arquivo a cada clique; a aba salva quando é fechada.
            Config.SaveOnConfigSet = false;

            Enabled = Config.Bind("Geral", "Ativado", true,
                "Liga/desliga a compra automática.");
            ToggleKey = Config.Bind("Geral", "TeclaLigaDesliga", new KeyboardShortcut(KeyCode.F9),
                "Tecla para ligar/desligar durante o jogo.");
            EmptySeconds = Config.Bind("Regras", "SegundosSemEstoque", 20f,
                new ConfigDescription("Quantos segundos seguidos o produto precisa ficar abaixo do limite antes de comprar.",
                    new AcceptableValueRange<float>(1f, 3600f)));
            CheckInterval = Config.Bind("Regras", "IntervaloDeVerificacao", 1f,
                new ConfigDescription("De quantos em quantos segundos o mod confere o estoque.",
                    new AcceptableValueRange<float>(0.25f, 60f)));
            StockThreshold = Config.Bind("Regras", "LimiteDeUnidades", 0,
                new ConfigDescription("Compra quando (estoque + caixas + caixas a caminho) tiver esta quantidade de unidades ou menos. 0 = só quando zerar.",
                    new AcceptableValueRange<int>(0, 1000)));
            BoxesPerOrder = Config.Bind("Regras", "CaixasPorPedido", 1,
                new ConfigDescription("Quantas caixas comprar de cada vez.", new AcceptableValueRange<int>(1, 50)));
            MinFundsReserve = Config.Bind("Dinheiro", "ReservaMinimaDeDinheiro", 0f,
                new ConfigDescription("Não compra se o saldo ficar abaixo deste valor depois da compra.",
                    new AcceptableValueRange<float>(0f, 1000000f)));
            MaxSpendPerDay = Config.Bind("Dinheiro", "GastoMaximoPorDia", 0f,
                new ConfigDescription("Limite de gasto automático por dia do jogo. 0 = sem limite.",
                    new AcceptableValueRange<float>(0f, 1000000f)));
            CooldownSeconds = Config.Bind("Regras", "IntervaloEntreComprasDoMesmoProduto", 30f,
                new ConfigDescription("Tempo mínimo (segundos) entre duas compras automáticas do mesmo produto.",
                    new AcceptableValueRange<float>(0f, 3600f)));
            RequireEmptyShelves = Config.Bind("Regras", "ExigirPrateleiraVazia", false,
                "Se true, só compra quando o produto também estiver zerado nas prateleiras.");
            OnlyWhenStoreOpen = Config.Bind("Regras", "SomenteComLojaAberta", false,
                "Se true, só compra enquanto a loja estiver aberta.");

            _boardTab = new BoardTab(this);

            Log.LogInfo($"{PluginName} {PluginVersion} carregado. Aba 'Reposição' no Quadro de Gestão | Liga/desliga: {ToggleKey.Value} | Ativado: {Enabled.Value}");
        }

        private void Update()
        {
            HandleKeys();
            TickBoardTab();

            if (Time.time < _nextCheck)
                return;
            _nextCheck = Time.time + CheckInterval.Value;

            try
            {
                Check();
                _loggedScanError = false;
            }
            catch (Exception e)
            {
                Status = "Erro na verificação (veja o log)";
                if (!_loggedScanError)
                {
                    _loggedScanError = true;
                    Log.LogError($"Erro na verificação de estoque: {e}");
                }
            }
        }

        private void TickBoardTab()
        {
            try
            {
                if (_blackboard == null && GameData.Instance != null)
                    _blackboard = GameData.Instance.GetComponent<ManagerBlackboard>();
                _boardTab.Tick(_blackboard);
            }
            catch (Exception e)
            {
                if (!_loggedTabError)
                {
                    _loggedTabError = true;
                    Log.LogError($"Erro na aba do Quadro de Gestão: {e}");
                }
            }
        }

        private void HandleKeys()
        {
            if (_inputBroken)
                return;
            try
            {
                if (ToggleKey.Value.IsDown())
                    SetEnabled(!Enabled.Value);
            }
            catch (InvalidOperationException)
            {
                // Jogo configurado só com o novo Input System: teclas desativadas, a config continua valendo.
                _inputBroken = true;
                Log.LogWarning("Input legado indisponível; use o arquivo de configuração.");
            }
        }

        internal void SetEnabled(bool value)
        {
            Enabled.Value = value;
            EmptySince.Clear();
            Config.Save();
            Log.LogMessage($"AutoRestock {(value ? "LIGADO" : "DESLIGADO")}");
        }

        internal void SaveConfig() => Config.Save();

        internal void ResetDefaults()
        {
            foreach (ConfigEntryBase entry in new ConfigEntryBase[]
                     {
                         EmptySeconds, CheckInterval, StockThreshold, BoxesPerOrder, MinFundsReserve,
                         MaxSpendPerDay, CooldownSeconds, RequireEmptyShelves, OnlyWhenStoreOpen
                     })
            {
                entry.BoxedValue = entry.DefaultValue;
            }
            Config.Save();
        }

        private void Check()
        {
            if (!NetworkServer.active)
            {
                Status = NetworkClient.active
                    ? "Inativo: só funciona para quem hospeda a partida"
                    : "Aguardando partida...";
                EmptySince.Clear();
                return;
            }

            GameData gameData = GameData.Instance;
            ProductListing listing = ProductListing.Instance;
            ManagerBlackboard blackboard = gameData != null ? gameData.GetComponent<ManagerBlackboard>() : null;
            if (gameData == null || listing == null || blackboard == null)
            {
                Status = "Aguardando partida...";
                return;
            }

            if (gameData.gameDay != _currentDay)
            {
                _currentDay = gameData.gameDay;
                SpentToday = 0f;
                BoxesToday = 0;
            }

            if (!Enabled.Value)
            {
                Status = "Desligado";
                EmptySince.Clear();
                return;
            }

            if (OnlyWhenStoreOpen.Value && !gameData.isSupermarketOpen)
            {
                Status = "Pausado: loja fechada";
                EmptySince.Clear();
                return;
            }

            if (!ScanExistences(blackboard))
                return;

            Status = "Ativo";
            float now = Time.time;
            int threshold = StockThreshold.Value;

            foreach (int productId in listing.availableProducts)
            {
                // Só considera produtos com etiqueta em alguma prateleira ou estoque,
                // senão o mod compraria todo produto desbloqueado que você nunca vende.
                bool needsRestock = false;
                if (_labeled.Contains(productId))
                {
                    int perBox = listing.productsData[productId].maxItemsPerBox;
                    int units = Get(_storage, productId) + Get(_boxes, productId) + Get(_pendingBoxes, productId) * perBox;
                    needsRestock = units <= threshold
                                   && (!RequireEmptyShelves.Value || Get(_shelves, productId) == 0);
                }

                if (!needsRestock)
                {
                    EmptySince.Remove(productId);
                    continue;
                }

                if (!EmptySince.TryGetValue(productId, out float since))
                {
                    EmptySince[productId] = now;
                    continue;
                }

                if (now - since < EmptySeconds.Value)
                    continue;

                if (_lastPurchase.TryGetValue(productId, out float last) && now - last < CooldownSeconds.Value)
                    continue;

                if (TryBuy(gameData, blackboard, productId, now))
                {
                    _lastPurchase[productId] = now;
                    EmptySince.Remove(productId);
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
            _pendingBoxes.Clear();
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
                    Add(_pendingBoxes, id, 1);
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

        private bool TryBuy(GameData gameData, ManagerBlackboard blackboard, int productId, float now)
        {
            int boxes = BoxesPerOrder.Value;
            float total = PricePerBox(blackboard, productId) * boxes;
            if (total <= 0f)
                return false;

            string name = ProductName(productId);

            string blocked = null;
            if (gameData.gameFunds - total < MinFundsReserve.Value)
                blocked = $"sem dinheiro (precisa ${total:0.00}, saldo ${gameData.gameFunds:0.00}, reserva ${MinFundsReserve.Value:0})";
            else if (MaxSpendPerDay.Value > 0f && SpentToday + total > MaxSpendPerDay.Value)
                blocked = $"limite diário atingido (gasto ${SpentToday:0.00} de ${MaxSpendPerDay.Value:0})";

            if (blocked != null)
            {
                if (!_lastNoFundsWarning.TryGetValue(productId, out float warned) || now - warned > 60f)
                {
                    _lastNoFundsWarning[productId] = now;
                    Log.LogWarning($"Não comprou {name}: {blocked}");
                    AddHistory($"[X] {name}: {blocked}");
                }
                return false;
            }

            // Mesmo efeito de ManagerBlackboard.BuyCargo, sem mexer na lista de compras do tablet.
            for (int i = 0; i < boxes; i++)
                blackboard.idsToSpawn.Add(productId);
            blackboard.SpawnRemainingCargoFromAutosave();

            gameData.CmdAlterFunds(-total);
            gameData.CmdmoneySpentOnProducts(total);

            SpentToday += total;
            BoxesToday += boxes;
            Log.LogMessage($"Comprado automaticamente: {boxes}x caixa de {name} (ID {productId}) por ${total:0.00}");
            AddHistory($"[OK] {boxes}x {name}  ${total:0.00}");
            return true;
        }

        private void AddHistory(string line)
        {
            string time = GameData.Instance != null ? FormatHour(GameData.Instance.timeOfDay) : "--:--";
            History.Insert(0, $"[{time}] {line}");
            if (History.Count > MaxHistory)
                History.RemoveAt(History.Count - 1);
        }

        private static string FormatHour(float timeOfDay)
        {
            int h = Mathf.FloorToInt(timeOfDay);
            int m = (int)((timeOfDay - h) * 60f);
            return $"{h:00}:{m:00}";
        }

        internal static string ProductName(int productId)
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
                // Nome é só para exibição; ignora falhas de localização.
            }
            return "produto " + productId;
        }

        private static int Get(Dictionary<int, int> counts, int id) =>
            counts.TryGetValue(id, out int value) ? value : 0;

        private static void Add(Dictionary<int, int> counts, int id, int amount) =>
            counts[id] = Get(counts, id) + amount;
    }
}
