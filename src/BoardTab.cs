using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using HutongGames.PlayMaker;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AutoRestock
{
    /// <summary>
    /// Aba "Reposição" dentro do Quadro de Gestão (Canvas_Manager), montada com clones dos elementos do próprio jogo.
    ///
    /// Hierarquia do jogo (cena level1):
    ///   Canvas_Manager/Buttons_Bar/Button_ProductOrder ... Button_Invoices, CloseButton
    ///   Canvas_Manager/Tabs/ProductsOrder_Tab ... Invoices_Tab        (Tabs == ManagerBlackboard.tabsOBJ)
    /// Cada botão de aba tem uma FSM do PlayMaker que desliga todos os filhos de Tabs e liga o filho de índice
    /// "TabIndex". A nossa aba é o último filho de Tabs e o nosso botão é um clone de Button_Invoices com outro
    /// TabIndex, então o próprio jogo cuida de mostrar/esconder as abas.
    /// </summary>
    internal class BoardTab
    {
        private const string TabName = "AutoRestock_Tab";
        private const string ButtonName = "Button_AutoRestock";
        private const string TabTitle = "Reposição";
        private const float RefreshInterval = 0.25f;

        // Layout em unidades do canvas (centro do quadro = 0,0; área útil ~ x -565..565, y -250..265).
        private const float LeftX = -425f, LeftW = 250f;
        private const float RowX = -50f, RowW = 430f, MinusX = 195f, PlusX = 243f, StepW = 40f, ToggleX = 219f, ToggleW = 88f;
        private const float RightX = 432f, RightW = 260f;
        private const float RowTop = 235f, RowStep = 54f, RowH = 40f;

        private static readonly string[] GameTabButtons =
            { "Button_ProductOrder", "Button_Supermarket", "Button_Employees", "Button_Statistics", "Button_Invoices" };

        private readonly Plugin _plugin;
        private readonly List<Action> _refreshers = new List<Action>();

        private Transform _tabs;
        private Transform _failedFor;
        private GameObject _tab;
        private GameObject _tabButton;
        private bool _wasVisible;
        private float _nextRefresh;

        private GameObject _tplHeader, _tplRow, _tplButton, _tplSeparator;
        private float _boxFontSize;

        public BoardTab(Plugin plugin)
        {
            _plugin = plugin;
        }

        private bool IsVisible => _tab != null && _tab.activeInHierarchy;

        /// <summary>Chamado todo frame. Cria a aba quando o quadro existe e atualiza os textos enquanto ela está visível.</summary>
        public void Tick(ManagerBlackboard blackboard)
        {
            if (_tab == null && blackboard != null && blackboard.tabsOBJ != null)
            {
                Transform tabs = blackboard.tabsOBJ.transform;
                if (_failedFor != tabs)
                {
                    try
                    {
                        Build(tabs);
                        Plugin.Log.LogInfo($"Aba \"{TabTitle}\" adicionada ao Quadro de Gestão.");
                    }
                    catch (Exception e)
                    {
                        _failedFor = tabs;
                        Plugin.Log.LogError($"Não foi possível criar a aba no Quadro de Gestão: {e}");
                        Cleanup();
                    }
                }
            }

            bool visible = IsVisible;
            if (_wasVisible && !visible)
                _plugin.SaveConfig(); // Saiu da aba ou fechou o quadro: grava as opções.
            if (visible && (!_wasVisible || Time.unscaledTime >= _nextRefresh))
                Refresh();
            _wasVisible = visible;
        }

        private void Refresh()
        {
            _nextRefresh = Time.unscaledTime + RefreshInterval;
            foreach (Action refresh in _refreshers)
                refresh();
        }

        private void Cleanup()
        {
            if (_tab != null)
                Object.Destroy(_tab);
            if (_tabButton != null)
                Object.Destroy(_tabButton);
            _tab = null;
            _tabButton = null;
            _refreshers.Clear();
        }

        // ------------------------------------------------------------------ montagem

        private void Build(Transform tabs)
        {
            _refreshers.Clear();
            _tabs = tabs;

            Transform canvas = tabs.parent;
            Transform bar = Find(canvas, "Buttons_Bar");
            Transform employees = Find(tabs, "Employees_Tab");
            _tplHeader = Find(employees, "RenewBCK").gameObject;
            _tplRow = Find(employees, "MaxEmployeesBCK").gameObject;
            _tplButton = Find(employees, "EmployeesButtons/Button_HireEmployees").gameObject;
            _tplSeparator = Find(employees, "Separator").gameObject;
            GameObject tplTabButton = Find(bar, "Button_Invoices").gameObject;

            _boxFontSize = Find(_tplRow.transform, "MaxEmployees_Text").GetComponent<TextMeshProUGUI>().fontSize * 0.75f;

            // Container da aba, inativo enquanto monta (assim os clones não rodam Awake/Start).
            _tab = new GameObject(TabName, typeof(RectTransform));
            _tab.SetActive(false);
            var tabRt = (RectTransform)_tab.transform;
            tabRt.SetParent(tabs, false);
            CopyRect((RectTransform)employees, tabRt);
            tabRt.SetAsLastSibling();
            int tabIndex = tabRt.GetSiblingIndex();

            BuildLeftColumn();
            BuildSettings();
            BuildRightColumn();

            Separator(-283f);
            Separator(283f);

            BuildTabButton(bar, tplTabButton, tabIndex);
            LayoutBar(bar);
        }

        private void BuildTabButton(Transform bar, GameObject template, int tabIndex)
        {
            // Clona dentro de um pai inativo para ajustar a FSM antes do Awake dela.
            var holder = new GameObject("AutoRestock_Holder", typeof(RectTransform));
            holder.SetActive(false);
            holder.transform.SetParent(bar, false);
            try
            {
                _tabButton = Object.Instantiate(template, holder.transform, false);
                _tabButton.name = ButtonName;

                Transform badge = _tabButton.transform.Find("NewInvoiceBCK");
                if (badge != null)
                    Object.DestroyImmediate(badge.gameObject);
                foreach (SetLocalizationString loc in _tabButton.GetComponentsInChildren<SetLocalizationString>(true))
                    Object.DestroyImmediate(loc);

                PlayMakerFSM fsm = _tabButton.GetComponent<PlayMakerFSM>();
                FsmInt index = fsm != null ? fsm.FsmVariables.FindFsmInt("TabIndex") : null;
                if (index != null)
                {
                    index.Value = tabIndex;
                }
                else
                {
                    Plugin.Log.LogWarning("FSM do botão de aba sem a variável TabIndex; usando troca de aba própria.");
                    if (fsm != null)
                        Object.DestroyImmediate(fsm);
                }

                TextMeshProUGUI label = _tabButton.GetComponentInChildren<TextMeshProUGUI>(true);
                label.text = TabTitle;

                // Garantia: se a FSM não abrir a nossa aba, abre manualmente.
                Button button = _tabButton.GetComponent<Button>();
                button.onClick.AddListener(() => _plugin.StartCoroutine(EnsureShown()));

                _tabButton.transform.SetParent(bar, false);
                _tabButton.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);
            }
            finally
            {
                Object.Destroy(holder);
            }
        }

        private IEnumerator EnsureShown()
        {
            // A FSM do jogo troca a aba no frame seguinte ao clique.
            for (int i = 0; i < 3; i++)
                yield return null;
            if (_tab == null || _tab.activeSelf || _tabs == null)
                yield break;
            foreach (Transform child in _tabs)
                child.gameObject.SetActive(false);
            _tab.SetActive(true);
        }

        /// <summary>Redistribui os botões da barra inferior para caber mais um, mantendo a largura total.</summary>
        private void LayoutBar(Transform bar)
        {
            var buttons = new List<RectTransform>();
            foreach (string name in GameTabButtons)
            {
                if (bar.Find(name) is RectTransform rt)
                    buttons.Add(rt);
            }
            if (buttons.Count == 0)
                return;

            float left = buttons.Min(b => b.anchoredPosition.x - b.sizeDelta.x / 2f);
            float right = buttons.Max(b => b.anchoredPosition.x + b.sizeDelta.x / 2f);
            buttons.Add((RectTransform)_tabButton.transform);

            const float gap = 10f;
            int n = buttons.Count;
            float width = (right - left - gap * (n - 1)) / n;

            for (int i = 0; i < n; i++)
            {
                RectTransform b = buttons[i];
                float ratio = width / b.sizeDelta.x;
                b.sizeDelta = new Vector2(width, b.sizeDelta.y);
                b.anchoredPosition = new Vector2(left + width / 2f + i * (width + gap), b.anchoredPosition.y);

                foreach (RectTransform child in b)
                {
                    child.sizeDelta = new Vector2(child.sizeDelta.x * ratio, child.sizeDelta.y);
                    child.anchoredPosition = new Vector2(child.anchoredPosition.x * ratio, child.anchoredPosition.y);
                }
                foreach (TextMeshProUGUI text in b.GetComponentsInChildren<TextMeshProUGUI>(true))
                    ShrinkToFit(text);
            }
        }

        private void BuildLeftColumn()
        {
            Header("Reposição automática", new Vector2(LeftX, 210f), new Vector2(LeftW, 75f));

            RectTransform toggle = MakeButton("", new Vector2(LeftX, 132f), new Vector2(LeftW, 60f),
                () => _plugin.SetEnabled(!_plugin.Enabled.Value));
            Image toggleImage = toggle.GetComponent<Image>();
            Color baseColor = toggleImage.color;
            TextMeshProUGUI toggleText = toggle.GetComponentInChildren<TextMeshProUGUI>(true);
            _refreshers.Add(() =>
            {
                bool on = _plugin.Enabled.Value;
                toggleText.text = on ? "LIGADO" : "DESLIGADO";
                toggleImage.color = baseColor * (on ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.6f, 0.6f));
            });

            TextMeshProUGUI status = Box(new Vector2(LeftX, 50f), new Vector2(LeftW, 90f), true);
            _refreshers.Add(() => status.text = "Status: " + _plugin.Status);

            TextMeshProUGUI spent = Row("Gasto hoje", new Vector2(LeftX, -22f), LeftW, 110f);
            _refreshers.Add(() => spent.text = Money(_plugin.SpentToday));
            TextMeshProUGUI boxes = Row("Caixas hoje", new Vector2(LeftX, -70f), LeftW, 110f);
            _refreshers.Add(() => boxes.text = _plugin.BoxesToday.ToString());

            MakeButton("Restaurar padrões", new Vector2(LeftX, -145f), new Vector2(LeftW, 50f), _plugin.ResetDefaults);

            TextMeshProUGUI hint = Box(new Vector2(LeftX, -205f), new Vector2(LeftW, 40f), true);
            hint.horizontalAlignment = HorizontalAlignmentOptions.Center;
            _refreshers.Add(() => hint.text = $"{_plugin.ToggleKey.Value} liga/desliga de qualquer lugar");
        }

        private void BuildSettings()
        {
            Plugin p = _plugin;
            int row = 0;
            float Y() => RowTop - RowStep * row++;

            FloatSetting("Tempo abaixo do limite", p.EmptySeconds, 1f, 3600f, SecondsStep, Seconds, Y());
            IntSetting("Limite de unidades (0 = zerado)", p.StockThreshold, 0, 1000,
                v => v < 20 ? 1 : v < 100 ? 5 : 10, v => v == 0 ? "zerado" : v + " un.", Y());
            BoolSetting("Exigir prateleira vazia também", p.RequireEmptyShelves, Y());
            BoolSetting("Só comprar com a loja aberta", p.OnlyWhenStoreOpen, Y());
            IntSetting("Caixas por pedido", p.BoxesPerOrder, 1, 50, v => 1, v => v.ToString(), Y());
            FloatSetting("Intervalo entre compras do produto", p.CooldownSeconds, 0f, 3600f, SecondsStep, Seconds, Y());
            FloatSetting("Reserva mínima no caixa", p.MinFundsReserve, 0f, 1000000f, MoneyStep, Money, Y());
            FloatSetting("Gasto máximo por dia", p.MaxSpendPerDay, 0f, 1000000f, MoneyStep,
                v => v <= 0f ? "sem limite" : Money(v), Y());
            FloatSetting("Verificar estoque a cada", p.CheckInterval, 0.25f, 60f,
                v => v < 2f ? 0.25f : v < 10f ? 1f : 5f, v => v.ToString("0.##") + "s", Y());
        }

        private void BuildRightColumn()
        {
            Header("Prestes a comprar", new Vector2(RightX, 232f), new Vector2(RightW, 50f));
            TextMeshProUGUI pending = Box(new Vector2(RightX, 132f), new Vector2(RightW, 140f), false);
            _refreshers.Add(() => pending.text = PendingText());

            Header("Últimas ações", new Vector2(RightX, 28f), new Vector2(RightW, 50f));
            TextMeshProUGUI history = Box(new Vector2(RightX, -122f), new Vector2(RightW, 240f), false);
            _refreshers.Add(() => history.text = _plugin.History.Count == 0
                ? "Nenhuma compra ainda."
                : string.Join("\n", _plugin.History));
        }

        private string PendingText()
        {
            Dictionary<int, float> since = _plugin.EmptySince;
            if (since.Count == 0)
                return "Nada no momento.";

            const int max = 5;
            float now = Time.time;
            var sb = new StringBuilder();
            foreach (KeyValuePair<int, float> kv in since.OrderBy(kv => kv.Value).Take(max))
            {
                float left = Mathf.Max(0f, _plugin.EmptySeconds.Value - (now - kv.Value));
                sb.Append(Plugin.ProductName(kv.Key)).Append(": ").Append(left.ToString("0")).Append("s\n");
            }
            if (since.Count > max)
                sb.Append("... e mais ").Append(since.Count - max);
            return sb.ToString().TrimEnd('\n');
        }

        // ------------------------------------------------------------------ linhas de configuração

        private void FloatSetting(string label, ConfigEntry<float> entry, float min, float max,
            Func<float, float> stepFor, Func<float, string> format, float y)
        {
            TextMeshProUGUI value = Row(label, new Vector2(RowX, y), RowW, 120f);
            _refreshers.Add(() => value.text = format(entry.Value));

            void Change(int dir)
            {
                float v = Mathf.Clamp(entry.Value, min, max);
                float step = stepFor(dir > 0 ? v : v - 0.0001f);
                float next = Mathf.Round((v + dir * step) / step) * step;
                entry.Value = Mathf.Clamp(next, min, max);
                Refresh();
            }

            StepButton("-", new Vector2(MinusX, y), () => Change(-1));
            StepButton("+", new Vector2(PlusX, y), () => Change(1));
        }

        private void IntSetting(string label, ConfigEntry<int> entry, int min, int max,
            Func<int, int> stepFor, Func<int, string> format, float y)
        {
            TextMeshProUGUI value = Row(label, new Vector2(RowX, y), RowW, 120f);
            _refreshers.Add(() => value.text = format(entry.Value));

            void Change(int dir)
            {
                int v = Mathf.Clamp(entry.Value, min, max);
                int step = stepFor(dir > 0 ? v : v - 1);
                int next = (v + dir * step) / step * step;
                entry.Value = Mathf.Clamp(next, min, max);
                Refresh();
            }

            StepButton("-", new Vector2(MinusX, y), () => Change(-1));
            StepButton("+", new Vector2(PlusX, y), () => Change(1));
        }

        private void BoolSetting(string label, ConfigEntry<bool> entry, float y)
        {
            Row(label, new Vector2(RowX, y), RowW, 0f);
            RectTransform button = MakeButton("", new Vector2(ToggleX, y), new Vector2(ToggleW, RowH), () =>
            {
                entry.Value = !entry.Value;
                Refresh();
            });
            Image image = button.GetComponent<Image>();
            Color baseColor = image.color;
            TextMeshProUGUI text = button.GetComponentInChildren<TextMeshProUGUI>(true);
            _refreshers.Add(() =>
            {
                text.text = entry.Value ? "Sim" : "Não";
                image.color = entry.Value ? baseColor * new Color(0.6f, 1f, 0.6f) : baseColor;
            });
        }

        private static float SecondsStep(float v) => v < 10f ? 1f : v < 60f ? 5f : v < 300f ? 15f : 60f;

        private static float MoneyStep(float v) => v < 1000f ? 100f : v < 10000f ? 500f : 1000f;

        private static string Seconds(float v) =>
            v < 60f ? v.ToString("0") + "s" : $"{(int)(v / 60f)}min {v % 60f:0}s".Replace(" 0s", "");

        private static string Money(float v) => "$" + v.ToString("N2");

        // ------------------------------------------------------------------ peças clonadas do jogo

        private RectTransform Clone(GameObject template, string name, Vector2 pos, Vector2 size)
        {
            GameObject go = Object.Instantiate(template, _tab.transform, false);
            go.name = "AR_" + name;
            go.SetActive(true);

            // Tira o comportamento do jogo (FSMs e textos localizados); fica só o visual.
            foreach (PlayMakerFSM fsm in go.GetComponentsInChildren<PlayMakerFSM>(true))
                Object.DestroyImmediate(fsm);
            foreach (SetLocalizationString loc in go.GetComponentsInChildren<SetLocalizationString>(true))
                Object.DestroyImmediate(loc);

            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        private void Header(string text, Vector2 pos, Vector2 size)
        {
            RectTransform rt = Clone(_tplHeader, "Header", pos, size);
            TextMeshProUGUI label = rt.GetComponentInChildren<TextMeshProUGUI>(true);
            Stretch(label.rectTransform, 8f, 8f, 4f, 4f);
            label.horizontalAlignment = HorizontalAlignmentOptions.Center;
            label.verticalAlignment = VerticalAlignmentOptions.Middle;
            label.text = text;
            ShrinkToFit(label);
        }

        /// <summary>Linha no estilo "Máximo de funcionários [ 3 ]". Devolve o texto do valor (null se valueWidth = 0).</summary>
        private TextMeshProUGUI Row(string text, Vector2 pos, float width, float valueWidth)
        {
            RectTransform rt = Clone(_tplRow, "Row", pos, new Vector2(width, RowH));
            TextMeshProUGUI label = Find(rt, "MaxEmployees_Text").GetComponent<TextMeshProUGUI>();
            Stretch(label.rectTransform, 12f, valueWidth > 0f ? valueWidth + 14f : 12f, 2f, 2f);
            label.horizontalAlignment = HorizontalAlignmentOptions.Left;
            label.verticalAlignment = VerticalAlignmentOptions.Middle;
            label.text = text;
            ShrinkToFit(label);

            var numberBck = (RectTransform)Find(rt, "NumberBCK");
            if (valueWidth <= 0f)
            {
                numberBck.gameObject.SetActive(false);
                return null;
            }

            numberBck.anchorMin = numberBck.anchorMax = numberBck.pivot = new Vector2(1f, 0.5f);
            numberBck.anchoredPosition = new Vector2(-4f, 0f);
            numberBck.sizeDelta = new Vector2(valueWidth, RowH - 8f);

            TextMeshProUGUI value = numberBck.GetComponentInChildren<TextMeshProUGUI>(true);
            Stretch(value.rectTransform, 4f, 4f, 0f, 0f);
            value.horizontalAlignment = HorizontalAlignmentOptions.Center;
            value.verticalAlignment = VerticalAlignmentOptions.Middle;
            ShrinkToFit(value);
            return value;
        }

        /// <summary>Caixa de texto com o fundo das linhas do jogo (sem o número).</summary>
        private TextMeshProUGUI Box(Vector2 pos, Vector2 size, bool wrap)
        {
            RectTransform rt = Clone(_tplRow, "Box", pos, size);
            Find(rt, "NumberBCK").gameObject.SetActive(false);

            TextMeshProUGUI text = Find(rt, "MaxEmployees_Text").GetComponent<TextMeshProUGUI>();
            Stretch(text.rectTransform, 10f, 10f, 6f, 6f);
            text.horizontalAlignment = HorizontalAlignmentOptions.Left;
            text.verticalAlignment = wrap ? VerticalAlignmentOptions.Middle : VerticalAlignmentOptions.Top;
            text.enableAutoSizing = false;
            text.fontSize = _boxFontSize;
            text.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            text.overflowMode = wrap ? TextOverflowModes.Truncate : TextOverflowModes.Ellipsis;
            if (wrap)
            {
                text.fontSizeMax = _boxFontSize;
                text.fontSizeMin = _boxFontSize * 0.6f;
                text.enableAutoSizing = true;
            }
            text.text = "";
            return text;
        }

        private RectTransform MakeButton(string text, Vector2 pos, Vector2 size, Action onClick)
        {
            RectTransform rt = Clone(_tplButton, "Button", pos, size);
            TextMeshProUGUI label = rt.GetComponentInChildren<TextMeshProUGUI>(true);
            Stretch(label.rectTransform, 6f, 6f, 2f, 2f);
            label.horizontalAlignment = HorizontalAlignmentOptions.Center;
            label.verticalAlignment = VerticalAlignmentOptions.Middle;
            label.text = text;
            ShrinkToFit(label);

            Button button = rt.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            if (onClick != null)
                button.onClick.AddListener(() => onClick());
            return rt;
        }

        /// <summary>Botão "-"/"+" que repete enquanto estiver pressionado.</summary>
        private void StepButton(string text, Vector2 pos, Action onStep)
        {
            RectTransform rt = MakeButton(text, pos, new Vector2(StepW, RowH), null);
            rt.gameObject.AddComponent<HoldRepeat>().OnStep = onStep;
        }

        private void Separator(float x)
        {
            GameObject go = Object.Instantiate(_tplSeparator, _tab.transform, false);
            go.name = "AR_Separator";
            go.SetActive(true);
            var rt = (RectTransform)go.transform;
            rt.anchoredPosition = new Vector2(x, rt.anchoredPosition.y);
        }

        // ------------------------------------------------------------------ utilidades

        private static Transform Find(Transform parent, string path)
        {
            Transform t = parent != null ? parent.Find(path) : null;
            if (t == null)
                throw new InvalidOperationException($"Elemento do quadro não encontrado: {(parent != null ? parent.name : "?")}/{path}");
            return t;
        }

        private static void CopyRect(RectTransform from, RectTransform to)
        {
            to.anchorMin = from.anchorMin;
            to.anchorMax = from.anchorMax;
            to.pivot = from.pivot;
            to.anchoredPosition = from.anchoredPosition;
            to.sizeDelta = from.sizeDelta;
            to.localScale = from.localScale;
            to.localRotation = from.localRotation;
        }

        private static void Stretch(RectTransform rt, float left, float right, float top, float bottom)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>Mantém o tamanho de fonte do jogo, mas deixa o texto encolher se não couber.</summary>
        private static void ShrinkToFit(TextMeshProUGUI text)
        {
            if (text.enableAutoSizing)
                return;
            text.fontSizeMax = text.fontSize;
            text.fontSizeMin = text.fontSize * 0.5f;
            text.enableAutoSizing = true;
        }
    }

    /// <summary>Dispara ao pressionar e repete enquanto o botão continua pressionado.</summary>
    internal class HoldRepeat : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        private const float FirstDelay = 0.4f;
        private const float RepeatDelay = 0.07f;

        public Action OnStep;
        private bool _down;
        private float _next;

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left)
                return;
            _down = true;
            _next = Time.unscaledTime + FirstDelay;
            OnStep?.Invoke();
        }

        public void OnPointerUp(PointerEventData eventData) => _down = false;

        public void OnPointerExit(PointerEventData eventData) => _down = false;

        private void OnDisable() => _down = false;

        private void Update()
        {
            if (!_down || Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + RepeatDelay;
            OnStep?.Invoke();
        }
    }
}
