using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using ResoniteModLoader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;

namespace BaruHDLIntegration
{
    [HarmonyPatch(typeof(SessionControlDialog))]
    static class SessionControlDialogPatch
    {
        private static Slot? _hdlContentRoot;
        private static int _hdlTabValue;

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
        /// HDLタブの共通レイアウトをセットアップする
        /// 中央50%にコンテンツを配置し、ヘッダーテキストを追加
        /// </summary>
        private static UIBuilder SetupCenteredLayout(UIBuilder ui)
        {
            RadiantUI_Constants.SetupDefaultStyle(ui);

            var columns = ui.SplitHorizontally(0.25f, 0.5f, 0.25f);
            ui = new UIBuilder(columns[1]);
            RadiantUI_Constants.SetupDefaultStyle(ui);

            ui.VerticalLayout(4f, 0f);
            ui.FitContent(SizeFit.Disabled, SizeFit.PreferredSize);
            ui.Style.MinHeight = 24f;
            ui.Style.PreferredHeight = 24f;

            ui.Text("ヘッドレス操作", bestFit: true);

            return ui;
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

            _hdlContentRoot = ui.Root;

            BuildHdlTabInitialUI(ui);
        }

        /// <summary>
        /// HDLタブの初期UI（ローディング状態）を構築
        /// </summary>
        private static void BuildHdlTabInitialUI(UIBuilder ui)
        {
            ui = SetupCenteredLayout(ui);
            ui.Text("読み込み中...");

            var world = ui.Root.Engine.WorldManager.FocusedWorld;
            FetchAndBuildSessionUI(world);
        }

        /// <summary>
        /// セッション情報を取得してUIを構築
        /// </summary>
        private static void FetchAndBuildSessionUI(World world)
        {
            world.Coroutines.StartBackgroundTask(async () =>
            {
                var client = BaruHDLIntegration.GetClient();
                ResoniteMod.Msg($"Getting session details for world: {world.Name}({world.SessionId})");
                try
                {
                    var session = await client.GetSession(world.SessionId);
                    _hdlContentRoot?.RunSynchronously(() =>
                    {
                        if (_hdlContentRoot != null && !_hdlContentRoot.IsDestroyed)
                        {
                            BuildHdlTabContent(_hdlContentRoot, session!);
                        }
                    });
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // 404の場合は自分のヘッドレスではないだけなのでエラーではない
                    ResoniteMod.Msg($"Session not found on headless: {world.SessionId}");
                    _hdlContentRoot?.RunSynchronously(() =>
                    {
                        if (_hdlContentRoot != null && !_hdlContentRoot.IsDestroyed)
                        {
                            BuildHdlTabNotFound(_hdlContentRoot);
                        }
                    });
                }
                catch (Exception ex)
                {
                    ResoniteMod.Warn($"Failed to get session details: {ex}");
                    _hdlContentRoot?.RunSynchronously(() =>
                    {
                        if (_hdlContentRoot != null && !_hdlContentRoot.IsDestroyed)
                        {
                            BuildHdlTabError(_hdlContentRoot, ex.Message);
                        }
                    });
                }
            });
        }

        /// <summary>
        /// HDLタブのコンテンツUI（セッション情報取得後）を構築
        /// </summary>
        private static void BuildHdlTabContent(Slot contentRoot, Hdlctrl.V1.Session session)
        {
            contentRoot.DestroyChildren();
            var ui = SetupCenteredLayout(new UIBuilder(contentRoot));

            var saveButtonLabel = session.CurrentState?.CanSave == true ? "World.Actions.Save".AsLocaleKey() : "World.Actions.SaveAs".AsLocaleKey();
            var saveButton = ui.Button(OfficialAssets.Graphics.Icons.Dash.SaveWorld, saveButtonLabel);
            saveButton.LocalPressed += async (IButton button, ButtonEventData eventData) =>
            {
                var client = BaruHDLIntegration.GetClient();
                saveButton.Slot.RunSynchronously(() =>
                {
                    saveButton.Enabled = false;
                    saveButton.LabelText = "Saving...";
                });
                if (session.CurrentState?.CanSave == true)
                {
                    await client.SaveWorld(session.Id);
                }
                else if (session.CurrentState?.CanSaveAs == true)
                {
                    await client.SaveWorldAs(session.Id);
                }
                var updatedSession = await client.GetSession(session.Id);
                if (updatedSession?.CurrentState?.CanSave == true)
                {
                    saveButton.Slot.RunSynchronously(() =>
                    {
                        saveButton.Enabled = true;
                        saveButton.LabelText = "World.Actions.Save".AsLocaleKey().format;
                    });
                }
                else if (updatedSession?.CurrentState?.CanSaveAs == true)
                {
                    saveButton.Slot.RunSynchronously(() =>
                    {
                        saveButton.Enabled = true;
                        saveButton.LabelText = "World.Actions.SaveAs".AsLocaleKey().format;
                    });
                }
                else
                {
                    saveButton.Slot.RunSynchronously(() =>
                    {
                        saveButton.Enabled = false;
                        saveButton.LabelText = "World.Actions.SaveAs".AsLocaleKey().format;
                    });
                }
            };
            saveButton.Enabled = session.CurrentState?.CanSave == true || session.CurrentState?.CanSaveAs == true;

            var stopButton = ui.Button(OfficialAssets.Graphics.Icons.Dash.CloseWorld, "World.Actions.Close".AsLocaleKey());
            stopButton.LocalPressed += async (IButton button, ButtonEventData eventData) =>
            {
                var client = BaruHDLIntegration.GetClient();
                stopButton.Slot.RunSynchronously(() =>
                {
                    stopButton.Enabled = false;
                    stopButton.LabelText = "Stopping...";
                });
                await client.StopWorld(session.Id);
                stopButton.Slot.RunSynchronously(() =>
                {
                    stopButton.Enabled = true;
                    stopButton.LabelText = "World.Actions.Close".AsLocaleKey().format;
                });
            };

            var adminButton = ui.Button("Adminになる");
            adminButton.LocalPressed += async (IButton button, ButtonEventData eventData) =>
            {
                var client = BaruHDLIntegration.GetClient();
                adminButton.Slot.RunSynchronously(() =>
                {
                    adminButton.Enabled = false;
                    adminButton.LabelText = "リクエスト中...";
                });
                await client.UpdateUserRole(session.HostId, session.Id, adminButton.World.LocalUser.UserID, "Admin");
                adminButton.Slot.RunSynchronously(() =>
                {
                    adminButton.Enabled = true;
                    adminButton.LabelText = "Adminになる";
                });
            };
            adminButton.Enabled = contentRoot.Engine.WorldManager.FocusedWorld.LocalUser.Role.RoleName != "Admin";
        }

        /// <summary>
        /// HDLタブのセッション未検出UI（404の場合）
        /// </summary>
        private static void BuildHdlTabNotFound(Slot contentRoot)
        {
            contentRoot.DestroyChildren();
            var ui = SetupCenteredLayout(new UIBuilder(contentRoot));

            ui.Text("このセッションは設定されたcontrollerで管理されていません");

            var refreshButton = ui.Button("再取得");
            refreshButton.LocalPressed += (IButton button, ButtonEventData eventData) =>
            {
                if (_hdlContentRoot != null && !_hdlContentRoot.IsDestroyed)
                {
                    _hdlContentRoot.DestroyChildren();
                    var refreshUi = new UIBuilder(_hdlContentRoot);
                    BuildHdlTabInitialUI(refreshUi);
                }
            };
        }

        /// <summary>
        /// HDLタブのエラーUI
        /// </summary>
        private static void BuildHdlTabError(Slot contentRoot, string errorMessage)
        {
            contentRoot.DestroyChildren();
            var ui = SetupCenteredLayout(new UIBuilder(contentRoot));

            ui.Text($"エラー: {errorMessage}");

            var refreshButton = ui.Button("再取得");
            refreshButton.LocalPressed += (IButton button, ButtonEventData eventData) =>
            {
                if (_hdlContentRoot != null && !_hdlContentRoot.IsDestroyed)
                {
                    _hdlContentRoot.DestroyChildren();
                    var refreshUi = new UIBuilder(_hdlContentRoot);
                    BuildHdlTabInitialUI(refreshUi);
                }
            };
        }

        /// <summary>
        /// タブボタンの色を更新（HDLタブのハイライト対応）
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
        }

        /// <summary>
        /// ワールド変更時にHDLタブのUIを更新
        /// </summary>
        [HarmonyPatch("UpdateValueSyncs")]
        [HarmonyPostfix]
        public static void UpdateValueSyncs_Postfix(SessionControlDialog __instance, World world)
        {
            var activeTab = _activeTabField.GetValue(__instance) as Sync<SessionControlDialog.Tab>;
            if (activeTab == null) return;

            if ((int)activeTab.Value != _hdlTabValue || _hdlContentRoot == null) return;

            _hdlContentRoot.RunSynchronously(() =>
            {
                _hdlContentRoot.DestroyChildren();
            });

            FetchAndBuildSessionUI(world);
        }
    }
}
