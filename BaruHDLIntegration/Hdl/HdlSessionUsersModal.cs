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
    /// セッション内ユーザー管理モーダル (Kick/Ban/Role/Invite)
    /// </summary>
    internal static class HdlSessionUsersModal
    {
        private static readonly string[] _headers = { "Name", "Role", "Present" };
        private static readonly float[] _weights = { 24f, 12f, 8f };

        private static readonly string[] _roles = { "Admin", "Moderator", "Builder", "Guest", "Spectator" };

        internal static void Open(World invokerWorld, Hdlctrl.V1.Session session)
        {
            var world = invokerWorld.Engine.WorldManager.FocusedWorld ?? invokerWorld;
            world.RunSynchronously(() =>
            {
                var (rootSlot, ui) = HdlUI.BuildModalPanel(world, $"ユーザー管理: {session.Name}", new float2(1000f, 720f));
                BuildContent(rootSlot, ui, session);
            });
        }

        private static void BuildContent(Slot rootSlot, UIBuilder ui, Hdlctrl.V1.Session session)
        {
            HdlUI.BuildReadOnlyField(ui, "Session ID", session.Id);
            HdlUI.BuildReadOnlyField(ui, "Host ID", session.HostId);

            // 招待セクション
            ui.Text("--- ユーザー招待 ---", bestFit: true);
            var inviteNameField = ui.HorizontalElementWithLabel("ユーザー名 or ID", 0.4f, () => ui.TextField());
            inviteNameField.TargetString = "";

            var statusText = HdlUI.BuildStatusText(ui);

            var inviteBtn = ui.Button("招待 (ユーザー名で送信)");
            inviteBtn.LocalPressed += async (b, e) =>
            {
                if (string.IsNullOrWhiteSpace(inviteNameField.TargetString))
                {
                    HdlUI.SetStatus(statusText, "ユーザー名 or ID を入力してください", true);
                    return;
                }
                await HdlUI.RunWithBusyButton(inviteBtn, "招待中...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    var target = inviteNameField.TargetString.Trim();
                    var req = new Hdlctrl.V1.InviteUserRequest
                    {
                        HostId = session.HostId,
                        SessionId = session.Id,
                        UserName = target.StartsWith("U-") ? null : target,
                        UserId = target.StartsWith("U-") ? target : null,
                    };
                    await client.InviteUserAsync(req);
                }, (msg, isError) => HdlUI.SetStatus(statusText, isError ? msg : "招待を送信しました", isError));
            };

            ui.Text("--- セッション内ユーザー ---", bestFit: true);

            var refreshBtn = ui.Button("更新");

            // ユーザー一覧コンテナ
            var listContainer = rootSlot.AddSlot("UserList");
            var rt = listContainer.AttachComponent<RectTransform>();
            var le = listContainer.AttachComponent<LayoutElement>();
            le.MinHeight.Value = 360f;
            le.FlexibleHeight.Value = 1f;
            listContainer.AttachComponent<VerticalLayout>();
            listContainer.AttachComponent<ContentSizeFitter>().VerticalFit.Value = SizeFit.PreferredSize;

            void Refresh()
            {
                rootSlot.World.Coroutines.StartBackgroundTask(async () =>
                {
                    List<UserInSession>? users = null;
                    string? error = null;
                    try
                    {
                        var client = BaruHDLIntegration.GetClient();
                        var res = await client.ListUsersInSessionAsync(new Hdlctrl.V1.ListUsersInSessionRequest { HostId = session.HostId, SessionId = session.Id });
                        users = res.Users ?? new List<UserInSession>();
                    }
                    catch (Exception ex)
                    {
                        ResoniteMod.Error($"Failed to list users: {ex}");
                        error = ex.Message;
                    }
                    listContainer.RunSynchronously(() =>
                    {
                        if (listContainer.IsDestroyed) return;
                        listContainer.DestroyChildren();
                        var listUi = new UIBuilder(listContainer);
                        RadiantUI_Constants.SetupDefaultStyle(listUi);

                        if (error != null)
                        {
                            listUi.Style.MinHeight = 32f;
                            listUi.Style.PreferredHeight = 32f;
                            listUi.Text($"エラー: {error}");
                            return;
                        }
                        if (users == null || users.Count == 0)
                        {
                            listUi.Style.MinHeight = 32f;
                            listUi.Style.PreferredHeight = 32f;
                            listUi.Text("ユーザーがいません");
                            return;
                        }

                        listUi.Style.MinHeight = 28f;
                        listUi.Style.PreferredHeight = 28f;
                        HdlUI.BuildListHeader(listUi, _headers, _weights, hasTrailingButton: false);

                        for (int i = 0; i < users.Count; i++)
                        {
                            var user = users[i];
                            // ユーザー行 (Name / Role / Present)
                            var cells = new[]
                            {
                                user.Name,
                                user.Role,
                                user.IsPresent ? "Yes" : "No",
                            };
                            listUi.Style.MinHeight = 32f;
                            listUi.Style.PreferredHeight = 32f;
                            listUi.Style.FlexibleHeight = -1f;
                            HdlUI.BuildListRow(listUi, cells, _weights, rowIndex: i, onClick: null);

                            // 操作行 (Role変更ボタン + Kick + Ban)
                            listUi.Style.MinHeight = 32f;
                            listUi.Style.PreferredHeight = 32f;
                            listUi.HorizontalLayout(4f, 0f, 8f, 0f, 8f);
                            listUi.Style.MinWidth = 80f;
                            listUi.Style.FlexibleWidth = -1f;
                            listUi.Text("Role:", bestFit: true, Alignment.MiddleLeft);
                            listUi.Style.MinWidth = -1f;
                            listUi.Style.FlexibleWidth = 100f;
                            var roleSelector = HdlUI.BuildArrowSelector(rootSlot, listUi, _roles.ToList(), Math.Max(0, Array.IndexOf(_roles, user.Role)));
                            listUi.Style.FlexibleWidth = -1f;
                            listUi.Style.MinWidth = 100f;
                            var setRoleBtn = listUi.Button("Role変更");
                            setRoleBtn.LocalPressed += async (bb, ee) =>
                            {
                                await HdlUI.RunWithBusyButton(setRoleBtn, "変更中...", async () =>
                                {
                                    var client = BaruHDLIntegration.GetClient();
                                    await client.UpdateUserRoleAsync(new Hdlctrl.V1.UpdateUserRoleRequest
                                    {
                                        HostId = session.HostId,
                                        Parameters = new Headless.Rpc.UpdateUserRoleRequest
                                        {
                                            SessionId = session.Id,
                                            UserId = user.Id,
                                            Role = _roles[roleSelector.Value.Value],
                                        },
                                    });
                                    Refresh();
                                }, (msg, isError) =>
                                {
                                    HdlUI.SetStatus(statusText, isError ? msg : $"{user.Name} の Role を変更しました", isError);
                                });
                            };
                            var kickBtn = listUi.Button("Kick");
                            kickBtn.LocalPressed += async (bb, ee) =>
                            {
                                await HdlUI.RunWithBusyButton(kickBtn, "Kicking...", async () =>
                                {
                                    var client = BaruHDLIntegration.GetClient();
                                    await client.KickUserAsync(new Hdlctrl.V1.KickUserRequest
                                    {
                                        HostId = session.HostId,
                                        Parameters = new Headless.Rpc.KickUserRequest
                                        {
                                            SessionId = session.Id,
                                            UserId = user.Id,
                                        },
                                    });
                                    Refresh();
                                }, (msg, isError) => HdlUI.SetStatus(statusText, isError ? msg : $"{user.Name} を Kick しました", isError));
                            };
                            var banBtn = listUi.Button("Ban");
                            banBtn.LocalPressed += async (bb, ee) =>
                            {
                                await HdlUI.RunWithBusyButton(banBtn, "Banning...", async () =>
                                {
                                    var client = BaruHDLIntegration.GetClient();
                                    await client.BanUserAsync(new Hdlctrl.V1.BanUserRequest
                                    {
                                        HostId = session.HostId,
                                        Parameters = new Headless.Rpc.BanUserRequest
                                        {
                                            SessionId = session.Id,
                                            UserId = user.Id,
                                        },
                                    });
                                    Refresh();
                                }, (msg, isError) => HdlUI.SetStatus(statusText, isError ? msg : $"{user.Name} を Ban しました", isError));
                            };
                            listUi.NestOut();
                        }
                    });
                });
            }

            refreshBtn.LocalPressed += (b, e) => Refresh();
            Refresh();

            var closeBtn = ui.Button("閉じる");
            closeBtn.LocalPressed += (b, e) =>
            {
                rootSlot.RunSynchronously(() => rootSlot.Destroy());
            };
        }
    }
}
