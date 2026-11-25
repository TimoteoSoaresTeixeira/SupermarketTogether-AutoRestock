using System;
using System.Linq;
using BepInEx.Configuration;
using HutongGames.PlayMaker;
using UnityEngine;

namespace AutoRestock
{
    /// <summary>
    /// Janela IMGUI dentro do jogo para mudar as opções do AutoRestock sem editar o .cfg.
    /// Enquanto aberta, libera o cursor e trava a câmera usando a mesma flag do menu de opções do jogo.
    /// </summary>
    internal class ConfigWindow
    {
        private const int WindowId = 0x41525354; // "ARST"

        private readonly Plugin _plugin;
        private Rect _rect = new Rect(40f, 40f, 440f, 10f);
        private bool _open;

        // Estado do jogo antes de abrir a janela, para restaurar ao fechar.
        private bool _weSetInOptions;
        private CursorLockMode _prevLock;
        private bool _prevVisible;

        private GUIStyle _header;
        private GUIStyle _small;

        public ConfigWindow(Plugin plugin)
        {
            _plugin = plugin;
        }

        public void Toggle()
        {
            if (_open)
                Close();
            else
                Open();
        }

        private void Open()
        {
            _open = true;
            _prevLock = Cursor.lockState;
            _prevVisible = Cursor.visible;

            FsmBool inOptions = InOptionsVar();
            if (inOptions != null && !inOptions.Value)
            {
                inOptions.Value = true;
                _weSetInOptions = true;
            }
        }

        public void Close()
        {
            if (!_open)
                return;
            _open = false;
            _plugin.SaveConfig();

            if (_weSetInOptions)
            {
                FsmBool inOptions = InOptionsVar();
                if (inOptions != null)
                    inOptions.Value = false;
                _weSetInOptions = false;
            }

            Cursor.lockState = _prevLock;
            Cursor.visible = _prevVisible;
        }

        public void Update()
        {
            if (!_open)
                return;
            // O jogo pode travar o cursor de novo; mantém liberado enquanto a janela estiver aberta.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        public void OnGUI()
        {
            if (!_open)
                return;

            if (_header == null)
            {
                _header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 13 };
                _small = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
            }

            _rect = GUILayout.Window(WindowId, _rect, Draw, $"AutoRestock {Plugin.PluginVersion}", GUILayout.Width(440f));
        }

        private void Draw(int id)
        {
            Plugin p = _plugin;

            // --- Liga/desliga + status ---
            GUILayout.BeginHorizontal();
            GUI.color = p.Enabled.Value ? Color.green : new Color(1f, 0.45f, 0.45f);
            if (GUILayout.Button(p.Enabled.Value ? "LIGADO (clique para desligar)" : "DESLIGADO (clique para ligar)", GUILayout.Height(28f)))
                p.SetEnabled(!p.Enabled.Value);
            GUI.color = Color.white;
            GUILayout.EndHorizontal();

            GUILayout.Label($"Status: {p.Status}", _small);
            GUILayout.Label($"Hoje: {p.BoxesToday} caixa(s), ${p.SpentToday:0.00} gastos", _small);

            // --- Quando comprar ---
            GUILayout.Space(6f);
            GUILayout.Label("Quando comprar", _header);
            FloatSlider("Tempo abaixo do limite", p.EmptySeconds, 1f, 300f, 1f, "0", "s");
            IntSlider("Limite de unidades (0 = zerado)", p.StockThreshold, 0, 200);
            BoolToggle("Exigir prateleira vazia também", p.RequireEmptyShelves);
            BoolToggle("Só comprar com a loja aberta", p.OnlyWhenStoreOpen);

            // --- Quanto comprar ---
            GUILayout.Space(6f);
            GUILayout.Label("Quanto comprar", _header);
            IntSlider("Caixas por pedido", p.BoxesPerOrder, 1, 20);
            FloatSlider("Intervalo entre compras do mesmo produto", p.CooldownSeconds, 0f, 600f, 5f, "0", "s");

            // --- Dinheiro ---
            GUILayout.Space(6f);
            GUILayout.Label("Dinheiro", _header);
            FloatSlider("Reserva mínima no caixa", p.MinFundsReserve, 0f, 20000f, 50f, "0", "", "$");
            FloatSlider("Gasto máximo por dia (0 = sem limite)", p.MaxSpendPerDay, 0f, 50000f, 50f, "0", "", "$");

            // --- Verificação ---
            GUILayout.Space(6f);
            GUILayout.Label("Verificação", _header);
            FloatSlider("Verificar estoque a cada", p.CheckInterval, 0.25f, 30f, 0.25f, "0.00", "s");

            // --- Contagem regressiva ---
            if (p.EmptySince.Count > 0)
            {
                GUILayout.Space(6f);
                GUILayout.Label("Prestes a comprar", _header);
                float now = Time.time;
                foreach (var kv in p.EmptySince.OrderBy(kv => kv.Value).Take(6))
                {
                    float left = Mathf.Max(0f, p.EmptySeconds.Value - (now - kv.Value));
                    GUILayout.Label($"- {Plugin.ProductName(kv.Key)}: {left:0}s", _small);
                }
                if (p.EmptySince.Count > 6)
                    GUILayout.Label($"... e mais {p.EmptySince.Count - 6}", _small);
            }

            // --- Histórico ---
            if (p.History.Count > 0)
            {
                GUILayout.Space(6f);
                GUILayout.Label("Últimas ações", _header);
                foreach (string line in p.History)
                    GUILayout.Label(line, _small);
            }

            // --- Botões ---
            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Restaurar padrões"))
                p.ResetDefaults();
            if (GUILayout.Button($"Fechar ({p.WindowKey.Value})"))
                Close();
            GUILayout.EndHorizontal();

            GUILayout.Label($"{p.WindowKey.Value}: abre/fecha esta janela   {p.ToggleKey.Value}: liga/desliga", _small);

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        private void FloatSlider(string label, ConfigEntry<float> entry, float min, float max, float step,
            string format, string suffix, string prefix = "")
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(250f));
            GUILayout.FlexibleSpace();
            GUILayout.Label(prefix + entry.Value.ToString(format) + suffix, GUILayout.Width(70f));
            GUILayout.EndHorizontal();

            float raw = GUILayout.HorizontalSlider(Mathf.Clamp(entry.Value, min, max), min, max);
            float snapped = Mathf.Clamp((float)Math.Round(raw / step) * step, min, max);
            if (!Mathf.Approximately(snapped, Mathf.Clamp(entry.Value, min, max)))
                entry.Value = snapped;
        }

        private void IntSlider(string label, ConfigEntry<int> entry, int min, int max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(250f));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("-", GUILayout.Width(24f)) && entry.Value > min)
                entry.Value--;
            GUILayout.Label(entry.Value.ToString(), GUILayout.Width(40f));
            if (GUILayout.Button("+", GUILayout.Width(24f)) && entry.Value < max)
                entry.Value++;
            GUILayout.EndHorizontal();

            int v = Mathf.RoundToInt(GUILayout.HorizontalSlider(Mathf.Clamp(entry.Value, min, max), min, max));
            if (v != Mathf.Clamp(entry.Value, min, max))
                entry.Value = v;
        }

        private static void BoolToggle(string label, ConfigEntry<bool> entry)
        {
            bool v = GUILayout.Toggle(entry.Value, " " + label);
            if (v != entry.Value)
                entry.Value = v;
        }

        private static FsmBool InOptionsVar()
        {
            try
            {
                return FsmVariables.GlobalVariables.FindFsmBool("InOptions");
            }
            catch
            {
                return null;
            }
        }
    }
}
