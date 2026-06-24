using BaruHDLIntegration.Hdl;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;

namespace BaruHDLIntegration
{
    [HarmonyPatch(typeof(SessionControlDialog))]
    static class SessionControlDialogPatch
    {
        internal enum SubTab
        {
            Hosts = 0,
            Sessions = 1,
            Current = 2,
        }

        private static Slot? _hdlContentHost;
        private static Slot? _hdlTabRoot;
        private static Checkbox? _openInDashboardToggle;
        private static readonly Button?[] _subTabButtons = new Button?[Enum.GetValues(typeof(SubTab)).Length];
        private static SubTab _activeSubTab = SubTab.Current;
        private static int _hdlTabValue;

        /// <summary>
        /// モーダルをダッシュボード内オーバーレイで開く時の親スロット。
        /// 未初期化 / 破棄済みの場合は null (HdlUI.BuildModalPanel がワールド配置にフォールバックする)
        /// </summary>
        internal static Slot? GetDashboardOverlayParent()
            => (_hdlTabRoot != null && !_hdlTabRoot.IsDestroyed) ? _hdlTabRoot : null;

        private static readonly FieldInfo _activeTabField = AccessTools.Field(typeof(SessionControlDialog), "ActiveTab");
        private static readonly FieldInfo _tabButtonsField = AccessTools.Field(typeof(SessionControlDialog), "_tabButtons");
        private static readonly FieldInfo _slideSwapField = AccessTools.Field(typeof(SessionControlDialog), "_slideSwap");

        /// <summary>
        /// Tab enumの最大値 + 1 を計算して、HDLタブの値として使用
        /// </summary>
        private static int GetHdlTabValue()
        {
            var maxValue = Enum.GetValues(typeof(SessionControlDialog.Tab))
                .Cast<int>()
                .Max();
            return maxValue + 1;
        }

        /// <summary>
        /// セッションダイアログに「ヘッドレス」タブを追加する
        /// </summary>
        [HarmonyPatch("OnAttach")]
        [HarmonyPostfix]
        public static void OnAttach_Postfix(SessionControlDialog __instance)
        {
            _hdlTabValue = GetHdlTabValue();

            var tabButtons = _tabButtonsField.GetValue(__instance) as SyncRefList<Button>;
            if (tabButtons == null || tabButtons.Count == 0) return;

            var lastButton = tabButtons[tabButtons.Count - 1];
            if (lastButton?.Slot?.Parent == null) return;

            var ui = new UIBuilder(lastButton.Slot.Parent);
            RadiantUI_Constants.SetupDefaultStyle(ui);

            var hdlTab = ui.Button("ヘッドレス");
            hdlTab.LocalPressed += OnHdlTabClicked;
            tabButtons.Add(hdlTab);
        }

        /// <summary>
        /// HDLタブがクリックされた時の処理
        /// </summary>
        private static void OnHdlTabClicked(IButton button, ButtonEventData eventData)
        {
            var dialog = button.Slot.GetComponentInParents<SessionControlDialog>();
            if (dialog == null) return;

            var activeTab = _activeTabField.GetValue(dialog) as Sync<SessionControlDialog.Tab>;
            if (activeTab == null) return;

            if ((int)activeTab.Value == _hdlTabValue) return;

            var slideSwap = _slideSwapField.GetValue(dialog) as SyncRef<SlideSwapRegion>;
            if (slideSwap?.Target == null) return;

            int direction = _hdlTabValue.CompareTo((int)activeTab.Value);
            var slide = direction < 0 ? SlideSwapRegion.Slide.Right
                      : direction > 0 ? SlideSwapRegion.Slide.Left
                      : SlideSwapRegion.Slide.None;

            var ui = slideSwap.Target.Swap(slide);
            activeTab.Value = (SessionControlDialog.Tab)_hdlTabValue;

            BuildHdlTabRoot(ui);
        }

        /// <summary>
        /// ヘッドレスタブのルート: 左サイドバー(縦サブタブ+下部トグル)+右コンテンツの2カラム構成
        /// </summary>
        private static void BuildHdlTabRoot(UIBuilder ui)
        {
            RadiantUI_Constants.SetupDefaultStyle(ui);
            _hdlTabRoot = ui.Root;

            var cols = ui.SplitHorizontally(0.18f, 0.82f);

            var sideUi = new UIBuilder(cols[0]);
            RadiantUI_Constants.SetupDefaultStyle(sideUi);
            sideUi.VerticalLayout(4f, 4f, forceExpandHeight: false);

            // 上から積むタブボタン
            sideUi.Style.MinHeight = 36f;
            sideUi.Style.PreferredHeight = 36f;
            sideUi.Style.FlexibleHeight = -1f;
            _subTabButtons[(int)SubTab.Hosts] = HdlUI.BuildSubTabButton(sideUi, "ホスト", () => SwitchSubTab(SubTab.Hosts));
            _subTabButtons[(int)SubTab.Sessions] = HdlUI.BuildSubTabButton(sideUi, "セッション", () => SwitchSubTab(SubTab.Sessions));
            _subTabButtons[(int)SubTab.Current] = HdlUI.BuildSubTabButton(sideUi, "現在のセッション", () => SwitchSubTab(SubTab.Current));

            // 残り高さを占有するスペーサでトグルを最下部に押し下げる
            sideUi.Style.MinHeight = -1f;
            sideUi.Style.PreferredHeight = -1f;
            sideUi.Style.FlexibleHeight = 1f;
            sideUi.Empty("Spacer");

            // 最下部のトグル: ON でダッシュボード内モーダル、OFF でワールド配置モーダル
            sideUi.Style.MinHeight = 36f;
            sideUi.Style.PreferredHeight = 36f;
            sideUi.Style.FlexibleHeight = -1f;
            sideUi.HorizontalLayout(4f, 0f, 4f, 0f, 4f);
            sideUi.Style.MinWidth = -1f;
            sideUi.Style.FlexibleWidth = 1f;
            sideUi.Text("ダッシュボードで開く", bestFit: true, Alignment.MiddleLeft);
            sideUi.Style.FlexibleWidth = -1f;
            sideUi.Style.MinWidth = 36f;
            _openInDashboardToggle = sideUi.Checkbox(
                BaruHDLIntegration._config?.GetValue(BaruHDLIntegration.OpenModalsInDashboardKey) ?? false);
            sideUi.NestOut();

            UpdateSubTabButtonColors();

            _hdlContentHost = cols[1].Slot;

            RebuildActiveSubTab();
        }

        private static void SwitchSubTab(SubTab tab)
        {
            if (_activeSubTab == tab && _hdlContentHost != null && _hdlContentHost.ChildrenCount > 0) return;
            _activeSubTab = tab;
            UpdateSubTabButtonColors();
            RebuildActiveSubTab();
        }

        private static void RebuildActiveSubTab()
        {
            if (_hdlContentHost == null || _hdlContentHost.IsDestroyed) return;
            _hdlContentHost.DestroyChildren();
            switch (_activeSubTab)
            {
                case SubTab.Hosts:
                    HdlHostsPanel.Build(_hdlContentHost);
                    break;
                case SubTab.Sessions:
                    HdlSessionsPanel.Build(_hdlContentHost);
                    break;
                case SubTab.Current:
                    HdlCurrentSessionPanel.Build(_hdlContentHost);
                    break;
            }
        }

        private static void UpdateSubTabButtonColors()
        {
            for (int i = 0; i < _subTabButtons.Length; i++)
            {
                if (_subTabButtons[i] != null)
                {
                    HdlUI.SetSubTabButtonActive(_subTabButtons[i]!, i == (int)_activeSubTab);
                }
            }
        }

        /// <summary>
        /// タブボタンの色を更新（HDLタブのハイライト対応）。トグル状態の config 同期もここで行う
        /// </summary>
        [HarmonyPatch("OnCommonUpdate")]
        [HarmonyPostfix]
        public static void OnCommonUpdate_Postfix(SessionControlDialog __instance)
        {
            var activeTab = _activeTabField.GetValue(__instance) as Sync<SessionControlDialog.Tab>;
            var tabButtons = _tabButtonsField.GetValue(__instance) as SyncRefList<Button>;
            if (activeTab == null || tabButtons == null) return;

            var hdlTabIndex = tabButtons.Count - 1;
            if (hdlTabIndex >= 0)
            {
                var isActive = (int)activeTab.Value == _hdlTabValue;
                tabButtons[hdlTabIndex]?.SetColors(isActive
                    ? RadiantUI_Constants.TAB_ACTIVE_BACKGROUND_COLOR
                    : RadiantUI_Constants.TAB_INACTIVE_BACKGROUND_COLOR);
            }

            // トグル状態の変更を config に反映 (HDLタブが表示中のみチェック)
            if (_openInDashboardToggle != null && !_openInDashboardToggle.IsDestroyed && BaruHDLIntegration._config != null)
            {
                var saved = BaruHDLIntegration._config.GetValue(BaruHDLIntegration.OpenModalsInDashboardKey);
                if (saved != _openInDashboardToggle.IsChecked)
                {
                    BaruHDLIntegration._config.Set(BaruHDLIntegration.OpenModalsInDashboardKey, _openInDashboardToggle.IsChecked);
                }
            }
        }

        /// <summary>
        /// ワールド変更時、現在のセッションタブのみリフレッシュする
        /// (ホスト/セッションタブはワールド非依存なので維持)
        /// </summary>
        [HarmonyPatch("UpdateValueSyncs")]
        [HarmonyPostfix]
        public static void UpdateValueSyncs_Postfix(SessionControlDialog __instance, World world)
        {
            var activeTab = _activeTabField.GetValue(__instance) as Sync<SessionControlDialog.Tab>;
            if (activeTab == null) return;

            if ((int)activeTab.Value != _hdlTabValue) return;
            if (_activeSubTab != SubTab.Current) return;
            if (_hdlContentHost == null || _hdlContentHost.IsDestroyed) return;

            _hdlContentHost.RunSynchronously(() =>
            {
                if (_hdlContentHost == null || _hdlContentHost.IsDestroyed) return;
                _hdlContentHost.DestroyChildren();
                HdlCurrentSessionPanel.Build(_hdlContentHost);
            });
        }
    }
}
