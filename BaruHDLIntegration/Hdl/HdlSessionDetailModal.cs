using System;
using System.Collections.Generic;
using System.Linq;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Hdlctrl.V1;
using Headless.Rpc;
using ResoniteModLoader;

namespace BaruHDLIntegration.Hdl
{
    /// <summary>
    /// セッション詳細・編集・操作モーダル。controllerフロントエンドの SessionForm 相当
    /// </summary>
    internal static class HdlSessionDetailModal
    {
        private static readonly AccessLevel[] _accessLevels = new[]
        {
            AccessLevel.Private,
            AccessLevel.Lan,
            AccessLevel.Contacts,
            AccessLevel.ContactsPlus,
            AccessLevel.RegisteredUsers,
            AccessLevel.Anyone,
        };

        internal static void Open(World invokerWorld, Hdlctrl.V1.Session session, Action? onChanged = null)
        {
            var world = invokerWorld.Engine.WorldManager.FocusedWorld ?? invokerWorld;
            world.RunSynchronously(() =>
            {
                var (rootSlot, ui) = HdlUI.BuildModalPanel(world, $"セッション: {session.Name}", new float2(900f, 800f));
                BuildContent(rootSlot, ui, session, onChanged);
            });
        }

        private static void BuildContent(Slot rootSlot, UIBuilder ui, Hdlctrl.V1.Session session, Action? onChanged)
        {
            var isRunning = session.Status == SessionStatus.Running;
            var isEnded = session.Status == SessionStatus.Ended || session.Status == SessionStatus.Crashed;
            var current = session.CurrentState;
            var engine = rootSlot.World.Engine;

            HdlUI.BuildReadOnlyField(ui, "Session ID", session.Id);
            HdlUI.BuildReadOnlyField(ui, "Host ID", session.HostId);
            HdlUI.BuildReadOnlyField(ui, "Status", session.Status.ToString());
            HdlUI.BuildReadOnlyField(ui, "StartedAt", session.StartedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-");
            HdlUI.BuildReadOnlyField(ui, "EndedAt", session.EndedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-");

            // World/Session オーブ取得ボタン
            // 終了済みセッションは current が null なので StartupParameters.LoadWorldUrl をフォールバックに使う
            var worldUrl = !string.IsNullOrEmpty(current?.WorldUrl) ? current!.WorldUrl : session.StartupParameters?.LoadWorldUrl;
            var worldOrbBtn = ui.Button("ワールドオーブを取得");
            worldOrbBtn.Enabled = !string.IsNullOrEmpty(worldUrl);
            worldOrbBtn.LocalPressed += (b, e) =>
            {
                HdlUI.SpawnWorldOrb(engine, worldUrl, session.Name);
            };

            var sessionOrbBtn = ui.Button("セッションオーブを取得");
            sessionOrbBtn.Enabled = isRunning && current != null;
            sessionOrbBtn.LocalPressed += (b, e) =>
            {
                if (current == null) return;
                HdlUI.SpawnSessionOrb(engine, session.Id, current.ConnectUris, session.Name, current.UsersCount);
            };

            ui.Text("--- セッション設定 ---", bestFit: true);

            var nameField = ui.HorizontalElementWithLabel("Name", 0.4f, () => ui.TextField());
            nameField.TargetString = session.Name;

            var currentAccess = current?.AccessLevel ?? session.StartupParameters?.AccessLevel ?? AccessLevel.Private;
            var accessIndex = Array.IndexOf(_accessLevels, currentAccess);
            if (accessIndex < 0) accessIndex = 0;
            var accessSelector = ui.HorizontalElementWithLabel("Access Level", 0.4f, () =>
                HdlUI.BuildArrowSelector(rootSlot, ui, _accessLevels.Select(a => a.ToString()).ToList(), accessIndex));

            var maxUsersField = ui.HorizontalElementWithLabel("Max Users", 0.4f, () => ui.TextField());
            maxUsersField.TargetString = (current?.MaxUsers ?? session.StartupParameters?.MaxUsers ?? 0).ToString();

            var descriptionField = ui.HorizontalElementWithLabel("Description", 0.4f, () => ui.TextField());
            descriptionField.TargetString = current?.Description ?? session.StartupParameters?.Description ?? "";

            var tagsField = ui.HorizontalElementWithLabel("Tags (カンマ区切り)", 0.4f, () => ui.TextField());
            tagsField.TargetString = string.Join(",", current?.Tags ?? session.StartupParameters?.Tags ?? new List<string>());

            var statusText = HdlUI.BuildStatusText(ui);

            var saveParamsBtn = ui.Button("セッション設定を保存");
            saveParamsBtn.Enabled = isRunning;
            saveParamsBtn.LocalPressed += async (b, e) =>
            {
                await HdlUI.RunWithBusyButton(saveParamsBtn, "保存中...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    var inner = new Headless.Rpc.UpdateSessionParametersRequest
                    {
                        SessionId = session.Id,
                        Name = nameField.TargetString,
                        Description = descriptionField.TargetString,
                        AccessLevel = _accessLevels[accessSelector.Value.Value],
                        MaxUsers = int.TryParse(maxUsersField.TargetString, out var mu) ? mu : (int?)null,
                        UpdateTags = true,
                        Tags = tagsField.TargetString.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList(),
                    };
                    var req = new Hdlctrl.V1.UpdateSessionParametersRequest
                    {
                        HostId = session.HostId,
                        Parameters = inner,
                    };
                    await client.UpdateSessionParametersAsync(req);
                    onChanged?.Invoke();
                }, (msg, isError) => HdlUI.SetStatus(statusText, isError ? msg : "セッション設定を保存しました", isError));
            };

            ui.Text("--- 拡張設定 ---", bestFit: true);

            var memoField = ui.HorizontalElementWithLabel("Memo", 0.4f, () => ui.TextField());
            memoField.TargetString = session.Memo;

            var autoUpgradeField = ui.HorizontalElementWithLabel("Auto Upgrade", 0.4f, () => ui.Checkbox(true));
            autoUpgradeField.IsChecked = session.AutoUpgrade;

            var saveExtraBtn = ui.Button("拡張設定を保存");
            saveExtraBtn.LocalPressed += async (b, e) =>
            {
                await HdlUI.RunWithBusyButton(saveExtraBtn, "保存中...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    await client.UpdateSessionExtraSettingsAsync(new UpdateSessionExtraSettingsRequest
                    {
                        SessionId = session.Id,
                        Memo = memoField.TargetString,
                        AutoUpgrade = autoUpgradeField.IsChecked,
                    });
                    onChanged?.Invoke();
                }, (msg, isError) => HdlUI.SetStatus(statusText, isError ? msg : "拡張設定を保存しました", isError));
            };

            ui.Text("--- 操作 ---", bestFit: true);

            var saveOverwriteBtn = ui.Button("World保存 (Overwrite)");
            saveOverwriteBtn.Enabled = isRunning && current?.CanSave == true;
            saveOverwriteBtn.LocalPressed += async (b, e) =>
            {
                await HdlUI.RunWithBusyButton(saveOverwriteBtn, "Saving...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    await client.SaveSessionWorldAsync(new Hdlctrl.V1.SaveSessionWorldRequest
                    {
                        SessionId = session.Id,
                        SaveMode = Hdlctrl.V1.SaveSessionWorldRequest.Types.SaveMode.Overwrite,
                    });
                }, (msg, isError) => HdlUI.SetStatus(statusText, isError ? msg : "Save 完了", isError));
            };

            var saveAsBtn = ui.Button("World保存 (Save As)");
            saveAsBtn.Enabled = isRunning && current?.CanSaveAs == true;
            saveAsBtn.LocalPressed += async (b, e) =>
            {
                await HdlUI.RunWithBusyButton(saveAsBtn, "Saving as...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    await client.SaveSessionWorldAsync(new Hdlctrl.V1.SaveSessionWorldRequest
                    {
                        SessionId = session.Id,
                        SaveMode = Hdlctrl.V1.SaveSessionWorldRequest.Types.SaveMode.SaveAs,
                    });
                }, (msg, isError) => HdlUI.SetStatus(statusText, isError ? msg : "Save As 完了", isError));
            };

            var saveCopyBtn = ui.Button("World保存 (Copy)");
            saveCopyBtn.Enabled = isRunning;
            saveCopyBtn.LocalPressed += async (b, e) =>
            {
                await HdlUI.RunWithBusyButton(saveCopyBtn, "Copying...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    await client.SaveSessionWorldAsync(new Hdlctrl.V1.SaveSessionWorldRequest
                    {
                        SessionId = session.Id,
                        SaveMode = Hdlctrl.V1.SaveSessionWorldRequest.Types.SaveMode.Copy,
                    });
                }, (msg, isError) => HdlUI.SetStatus(statusText, isError ? msg : "Copy 完了", isError));
            };

            // 同じ設定で別ホスト/セッションとして開く
            var openSameBtn = ui.Button("同じ設定で開く (HdlStartWorldForm)");
            openSameBtn.LocalPressed += (b, e) =>
            {
                var startup = session.StartupParameters;
                var ctx = new StartWorldFormContext
                {
                    Title = $"同じ設定で開く: {session.Name}",
                    DefaultName = startup?.Name ?? session.Name,
                    LoadWorldUrl = startup?.LoadWorldUrl ?? current?.WorldUrl ?? "",
                    DefaultAccessLevel = startup?.AccessLevel ?? current?.AccessLevel ?? Headless.Rpc.AccessLevel.Private,
                    DefaultMaxUsers = startup?.MaxUsers ?? current?.MaxUsers,
                    DefaultDescription = startup?.Description ?? current?.Description,
                    DefaultTags = startup?.Tags ?? current?.Tags,
                    Memo = "Started by BaruHDLIntegration (copy of " + session.Id + ")",
                    OnStarted = _ => { onChanged?.Invoke(); },
                };
                HdlStartWorldForm.Open(rootSlot.World, ctx);
            };

            // ユーザー管理モーダルを開く
            var usersBtn = ui.Button("ユーザー管理");
            usersBtn.Enabled = isRunning;
            usersBtn.LocalPressed += (b, e) =>
            {
                HdlSessionUsersModal.Open(rootSlot.World, session);
            };

            var stopBtn = ui.Button("Stop (セッション終了)");
            stopBtn.Enabled = isRunning;
            stopBtn.LocalPressed += async (b, e) =>
            {
                await HdlUI.RunWithBusyButton(stopBtn, "Stopping...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    await client.StopSessionAsync(new Hdlctrl.V1.StopSessionRequest { SessionId = session.Id });
                    onChanged?.Invoke();
                    rootSlot.RunSynchronously(() => rootSlot.Destroy());
                }, (msg, isError) =>
                {
                    if (isError) HdlUI.SetStatus(statusText, msg, true);
                });
            };

            var deleteBtn = ui.Button("Delete (レコード削除)");
            deleteBtn.Enabled = isEnded;
            deleteBtn.LocalPressed += async (b, e) =>
            {
                await HdlUI.RunWithBusyButton(deleteBtn, "Deleting...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    await client.DeleteEndedSessionAsync(new DeleteEndedSessionRequest { SessionId = session.Id });
                    onChanged?.Invoke();
                    rootSlot.RunSynchronously(() => rootSlot.Destroy());
                }, (msg, isError) =>
                {
                    if (isError) HdlUI.SetStatus(statusText, msg, true);
                });
            };

            var closeBtn = ui.Button("閉じる");
            closeBtn.LocalPressed += (b, e) =>
            {
                rootSlot.RunSynchronously(() => rootSlot.Destroy());
            };
        }
    }
}
