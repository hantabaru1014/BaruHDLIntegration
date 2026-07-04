using BaruHDLIntegration.Hdl;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using Hdlctrl.V1;
using ResoniteModLoader;
using System;
using System.Collections.Generic;
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

        // グループセレクタ (BuildGroupSelector で BuildArrowSelector を包む)
        private static Slot? _groupSelectorSlot;

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

            // 残り高さを占有するスペーサでグループ選択/トグルを最下部に押し下げる
            sideUi.Style.MinHeight = -1f;
            sideUi.Style.PreferredHeight = -1f;
            sideUi.Style.FlexibleHeight = 1f;
            sideUi.Empty("Spacer");

            // グループセレクタ (ラベル + セレクタ + 再取得)
            sideUi.Style.MinHeight = 24f;
            sideUi.Style.PreferredHeight = 24f;
            sideUi.Style.FlexibleHeight = -1f;
            sideUi.Text("グループ:", bestFit: true, Alignment.MiddleLeft);

            sideUi.Style.MinHeight = 32f;
            sideUi.Style.PreferredHeight = 32f;
            BuildGroupSelector(sideUi);

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
        /// グループセレクタ。arrow selector (「全て」+ 各グループ) と「↻」再取得ボタンを並べる。
        /// 初期構築時はサイドバー側で親 slot を用意した状態から呼ばれる。
        /// </summary>
        private static void BuildGroupSelector(UIBuilder ui)
        {
            // 初期選択を config から復元 (初回構築時のみ)
            var savedGroupId = BaruHDLIntegration._config?.GetValue(BaruHDLIntegration.LastSelectedGroupIdKey);
            HdlSelectedGroup.SetSelectedGroupId(string.IsNullOrEmpty(savedGroupId) ? null : savedGroupId);

            _groupSelectorSlot = ui.Empty("GroupSelector");
            PopulateGroupSelectorContents();

            // 初回のみ ListGroups を叩く。以降はキャッシュを再利用し、再取得は ↻ ボタンで明示的に。
            if (HdlSelectedGroup.Groups.Count == 0) LoadGroupsAsync(force: false);
        }

        /// <summary>
        /// _groupSelectorSlot に arrow selector + reload ボタンを構築する。
        /// グループ一覧が更新された時は DestroyChildren してからこれを呼び直す。
        /// </summary>
        private static void PopulateGroupSelectorContents()
        {
            if (_groupSelectorSlot == null || _groupSelectorSlot.IsDestroyed) return;
            var rowUi = new UIBuilder(_groupSelectorSlot);
            RadiantUI_Constants.SetupDefaultStyle(rowUi);
            rowUi.HorizontalLayout(4f);

            // ラベル: index 0 = "全て" (null), 以降 HdlSelectedGroup.Groups と対応
            var groups = HdlSelectedGroup.Groups;
            var labels = new List<string>(groups.Count + 1) { "全て" };
            for (int i = 0; i < groups.Count; i++) labels.Add(HdlSelectedGroup.FormatGroupLabel(groups[i].Id));

            int defaultIndex = 0;
            var selectedId = HdlSelectedGroup.SelectedGroupId;
            if (selectedId != null)
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    if (groups[i].Id == selectedId) { defaultIndex = i + 1; break; }
                }
            }

            rowUi.Style.FlexibleWidth = 1f;
            rowUi.Style.MinWidth = -1f;
            HdlUI.BuildArrowSelector(_groupSelectorSlot, rowUi, labels, defaultIndex, onChange: newIndex =>
            {
                var selected = newIndex == 0 ? null : HdlSelectedGroup.Groups[newIndex - 1].Id;
                HdlSelectedGroup.SetSelectedGroupId(selected);
                BaruHDLIntegration._config?.Set(BaruHDLIntegration.LastSelectedGroupIdKey, selected ?? string.Empty);
                RebuildActiveSubTab();
            });

            rowUi.Style.FlexibleWidth = -1f;
            rowUi.Style.MinWidth = 40f;
            var reloadBtn = rowUi.Button("↻");
            reloadBtn.LocalPressed += (b, e) => LoadGroupsAsync(force: true);

            rowUi.NestOut();
        }

        private static void LoadGroupsAsync(bool force)
        {
            if (_hdlTabRoot == null || _hdlTabRoot.IsDestroyed) return;
            if (!force && HdlSelectedGroup.Groups.Count > 0) return;
            _hdlTabRoot.World.Coroutines.StartBackgroundTask(async () =>
            {
                List<Group>? groups = null;
                try
                {
                    var client = BaruHDLIntegration.GetClient();
                    var res = await client.GroupService.ListGroupsAsync(new ListGroupsRequest());
                    groups = res.Groups ?? new List<Group>();
                }
                catch (Exception ex)
                {
                    ResoniteMod.Warn($"Failed to list groups: {ex.Message}");
                }
                if (groups == null) return;

                _hdlTabRoot?.RunSynchronously(() =>
                {
                    HdlSelectedGroup.SetGroups(groups);
                    // 選択中 group_id が取得結果に無ければ「全て」に戻す
                    if (HdlSelectedGroup.SelectedGroupId != null && HdlSelectedGroup.FindGroup(HdlSelectedGroup.SelectedGroupId) == null)
                    {
                        HdlSelectedGroup.SetSelectedGroupId(null);
                    }
                    // ラベル反映のため selector を再構築
                    if (_groupSelectorSlot != null && !_groupSelectorSlot.IsDestroyed)
                    {
                        _groupSelectorSlot.DestroyChildren();
                        PopulateGroupSelectorContents();
                    }
                });
            });
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
