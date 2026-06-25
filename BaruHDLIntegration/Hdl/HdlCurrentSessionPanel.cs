using System;
using System.Net;
using System.Net.Http;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Hdlctrl.V1;
using ResoniteModLoader;

namespace BaruHDLIntegration.Hdl
{
    /// <summary>
    /// ヘッドレスタブの「現在のセッション」サブタブ。元 SessionControlDialogPatch.BuildHdlTabContent 系のロジックを移設
    /// </summary>
    internal static class HdlCurrentSessionPanel
    {
        private static Slot? _contentRoot;

        internal static void Build(Slot contentRoot)
        {
            _contentRoot = contentRoot;
            var ui = SetupCenteredLayout(new UIBuilder(contentRoot));
            ui.Text("読み込み中...");

            var world = contentRoot.Engine.WorldManager.FocusedWorld;
            FetchAndBuildSessionUI(world);
        }

        private static UIBuilder SetupCenteredLayout(UIBuilder ui)
        {
            RadiantUI_Constants.SetupDefaultStyle(ui);

            var columns = ui.SplitHorizontally(0.1f, 0.8f, 0.1f);
            ui = new UIBuilder(columns[1]);
            RadiantUI_Constants.SetupDefaultStyle(ui);

            ui.VerticalLayout(4f, 0f);
            ui.FitContent(SizeFit.Disabled, SizeFit.PreferredSize);
            ui.Style.MinHeight = 24f;
            ui.Style.PreferredHeight = 24f;

            ui.Text("ヘッドレス操作", bestFit: true);

            return ui;
        }

        private static void FetchAndBuildSessionUI(World world)
        {
            world.Coroutines.StartBackgroundTask(async () =>
            {
                var client = BaruHDLIntegration.GetClient();
                ResoniteMod.Msg($"Getting session details for world: {world.Name}({world.SessionId})");
                try
                {
                    var session = (await client.GetSessionDetailsAsync(new GetSessionDetailsRequest { SessionId = world.SessionId })).Session;
                    _contentRoot?.RunSynchronously(() =>
                    {
                        if (_contentRoot != null && !_contentRoot.IsDestroyed)
                        {
                            BuildContent(_contentRoot, session!);
                        }
                    });
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    ResoniteMod.Msg($"Session not found on headless: {world.SessionId}");
                    _contentRoot?.RunSynchronously(() =>
                    {
                        if (_contentRoot != null && !_contentRoot.IsDestroyed)
                        {
                            BuildNotFound(_contentRoot);
                        }
                    });
                }
                catch (Exception ex)
                {
                    ResoniteMod.Warn($"Failed to get session details: {ex}");
                    _contentRoot?.RunSynchronously(() =>
                    {
                        if (_contentRoot != null && !_contentRoot.IsDestroyed)
                        {
                            BuildError(_contentRoot, ex.Message);
                        }
                    });
                }
            });
        }

        private static void BuildContent(Slot contentRoot, Hdlctrl.V1.Session session)
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
                    await client.SaveSessionWorldAsync(new SaveSessionWorldRequest { SessionId = session.Id, SaveMode = SaveSessionWorldRequest.Types.SaveMode.Overwrite });
                }
                else if (session.CurrentState?.CanSaveAs == true)
                {
                    await client.SaveSessionWorldAsync(new SaveSessionWorldRequest { SessionId = session.Id, SaveMode = SaveSessionWorldRequest.Types.SaveMode.SaveAs });
                }
                var updatedSession = (await client.GetSessionDetailsAsync(new GetSessionDetailsRequest { SessionId = session.Id })).Session;
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
                await client.StopSessionAsync(new StopSessionRequest { SessionId = session.Id });
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
                await client.UpdateUserRoleAsync(new UpdateUserRoleRequest { HostId = session.HostId, Parameters = new Headless.Rpc.UpdateUserRoleRequest { SessionId = session.Id, UserId = adminButton.World.LocalUser.UserID, Role = "Admin" } });
                adminButton.Slot.RunSynchronously(() =>
                {
                    adminButton.Enabled = true;
                    adminButton.LabelText = "Adminになる";
                });
            };
            adminButton.Enabled = contentRoot.Engine.WorldManager.FocusedWorld.LocalUser.Role.RoleName != "Admin";
        }

        private static void BuildNotFound(Slot contentRoot)
        {
            contentRoot.DestroyChildren();
            var ui = SetupCenteredLayout(new UIBuilder(contentRoot));

            ui.Text("このセッションは設定されたcontrollerで管理されていません");

            var refreshButton = ui.Button("再取得");
            refreshButton.LocalPressed += (IButton button, ButtonEventData eventData) =>
            {
                if (_contentRoot != null && !_contentRoot.IsDestroyed)
                {
                    Build(_contentRoot);
                }
            };
        }

        private static void BuildError(Slot contentRoot, string errorMessage)
        {
            contentRoot.DestroyChildren();
            var ui = SetupCenteredLayout(new UIBuilder(contentRoot));

            ui.Text($"エラー: {errorMessage}");

            var refreshButton = ui.Button("再取得");
            refreshButton.LocalPressed += (IButton button, ButtonEventData eventData) =>
            {
                if (_contentRoot != null && !_contentRoot.IsDestroyed)
                {
                    Build(_contentRoot);
                }
            };
        }
    }
}
