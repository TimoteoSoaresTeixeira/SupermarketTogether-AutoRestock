using System;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace AutoRestock
{
    /// <summary>
    /// Painel IMGUI do AutoRestock que aparece junto com o Quadro de Gestão do jogo (ManagerBlackboard).
    /// Abre e fecha sozinho com o quadro; o próprio quadro já libera o cursor e trava a câmera.
    /// </summary>
    internal class ConfigWindow
    {
        private const int WindowId = 0x41525354; // "ARST"
        private const float Width = 400f;

        private readonly Plugin _plugin;
        private Rect _rect;
        private bool _visible;
        private bool _positioned;

        private GUIStyle _header;
        private GUIStyle _small;

        public ConfigWindow(Plugin plugin)
        {
            _plugin = plugin;
        }

        /// <summary>Chamado todo frame pelo plugin com o estado atual do quadro de gestão.</summary>
        public void SetBoardOpen(bool open)
        {
            if (open == _visible)
                return;
            _visible = open;
            if (!open)
            {
                // Quadro fechou: grava posição e opções no .cfg.
                _plugin.PanelX.Value = _rect.x;
                _plugin.PanelY.Value = _rect.y;
                _plugin.SaveConfig();
            }
        }

        public void OnGUI()
        {
            if (!_visible)
                return;

            if (_header == null)
            {
                _header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 13 };
                _small = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
            }

            if (!_positioned)
            {
                _positioned = true;
                // Posição salva; se não houver, encosta no canto direito da tela.
                float x = _plugin.PanelX.Value >= 0f ? _plugin.PanelX.Value : Screen.width - Width - 20f;
                float y = _plugin.PanelY.Value >= 0f ? _plugin.PanelY.Value : 80f;
                _rect = new Rect(x, y, Width, 10f);
            }

            // Mantém o painel dentro da tela (resolução pode mudar).
            _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width - _rect.width));
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height - 40f));

            string title = $"AutoRestock - compra automática {(_plugin.Enabled.Value ? "[LIGADO]" : "[DESLIGADO]")}";
            if (_plugin.PanelMinimized.Value)
                _rect = GUILayout.Window(WindowId, new Rect(_rect.x, _rect.y, Width, 10f), DrawMinimized, title, GUILayout.Width(Width));
            else
                _rect = GUILayout.Window(WindowId, _rect, Draw, title, GUILayout.Width(Width));
        }

        private void DrawMinimized(int id)
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_plugin.Enabled.Value ? "Desligar" : "Ligar"))
                _plugin.SetEnabled(!_plugin.Enabled.Value);
            if (GUILayout.Button("Mostrar opções"))
                _plugin.PanelMinimized.Value = false;
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
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
            if (GUILayout.Button("Minimizar"))
                p.PanelMinimized.Value = true;
            GUILayout.EndHorizontal();

            GUILayout.Label($"Arraste pela barra de título. {p.ToggleKey.Value} liga/desliga em qualquer lugar.", _small);

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        private void FloatSlider(string label, ConfigEntry<float> entry, float min, float max, float step,
            string format, string suffix, string prefix = "")
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(230f));
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
            GUILayout.Label(label, GUILayout.Width(230f));
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
    }
}
