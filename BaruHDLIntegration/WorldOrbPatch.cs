using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using Hdlctrl.V1;
using Headless.V1;
using ResoniteModLoader;
using SkyFrost.Base;

namespace BaruHDLIntegration
{
    /// <summary>
    /// WorldOrbをクリックしたときに出てくるContextMenuにヘッドレスで開くメニューを追加する
    /// </summary>
    [HarmonyPatch]
    class WorldOrbPatch
    {
        static Type TargetDisplayClass()
        {
            // FrooxEngine.WorldOrb+<>c__DisplayClass155_0+<<ToggleContextMenu>b__0>d を探したいので、まず c__DisplayClass155_0 をあてる
            return AccessTools.FirstInner(typeof(WorldOrb), t => AccessTools.FirstInner(t, t2 => t2.Name.Contains("ToggleContextMenu")) != null);
        }

        static Type TargetClass()
        {
            return AccessTools.FirstInner(TargetDisplayClass(), t => t.Name.Contains("ToggleContextMenu"));
        }

        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(TargetClass(), "MoveNext");
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var foundStartCustomSessionLdstr = false;
            var patched = false;

            foreach (var instruction in instructions)
            {
                if (instruction.Is(OpCodes.Ldstr, "World.Actions.StartCustomSession"))
                {
                    foundStartCustomSessionLdstr = true;
                }
                // StartCustomSession のMenuItem挿入が終わったpopの次に worldOrb と loc2 (ContextMenu menu) を入れて AddMenuItemを呼び出す
                if (!patched && foundStartCustomSessionLdstr && instruction.opcode == OpCodes.Pop)
                {
                    yield return instruction;
                    yield return new CodeInstruction(OpCodes.Ldloc_1);
                    yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(TargetDisplayClass(), "<>4__this"));
                    yield return new CodeInstruction(OpCodes.Ldloc_2);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(WorldOrbPatch), nameof(AddMenuItem)));
                    patched = true;
                }
                else
                {
                    yield return instruction;
                }
            }
        }

        static void AddMenuItem(WorldOrb worldOrb, ContextMenu menu)
        {
            var item = menu.AddItem("ヘッドレスでセッション開始", OfficialAssets.Graphics.Badges.Headless, null);
            item.Button.LocalPressed += (IButton button, ButtonEventData eventData) =>
            {
                try
                {
                    BuildWorldStartPanel(worldOrb);
                }
                catch (Exception ex)
                {
                    ResoniteMod.Error($"Failed to build world start panel: {ex}");
                    NotificationMessage.SpawnTextMessage($"Failed to build world start panel: {ex}", color: new colorX(1f, 0.2f, 0.3f));
                    return;
                }

                menu.RunSynchronously(() =>
                {
                    menu.LocalUser.CloseContextMenu(worldOrb);
                });
            };
        }

        private static async void BuildWorldStartPanel(WorldOrb worldOrb)
        {
            var client = BaruHDLIntegration.GetClient();
            string? hostListFetchError = null;
            var hosts = new List<HeadlessHost>();
            try
            {
                hosts = (await client.ListHeadlessHost()).Where(h => h.Status == Hdlctrl.V1.HeadlessHostStatus.Running).ToList();
            }
            catch (Exception ex)
            {
                ResoniteMod.Error($"Failed to get headless host list: {ex}");
                hostListFetchError = $"Failed to get headless host list: {ex}";
                return;
            }
            worldOrb.RunSynchronously(() =>
            {
                var rootSlot = worldOrb.World.AddSlot("Session Start", persistent: false);
                rootSlot.PositionInFrontOfUser(float3.Backward);
                var ui = RadiantUI_Panel.SetupPanel(rootSlot, "Start World by Headless", new float2(800f, 640f));
                rootSlot.LocalScale *= 0.0005f;
                RadiantUI_Constants.SetupEditorStyle(ui);
                rootSlot.SetContainerTitle("Start World by Headless");
                ui.ScrollArea();
                ui.VerticalLayout(4f, 0f);
                ui.FitContent(SizeFit.Disabled, SizeFit.PreferredSize);
                ui.Style.MinHeight = 32f;
                ui.Style.PreferredHeight = 32f;

                if (hostListFetchError != null)
                {
                    ui.Text(hostListFetchError);

                    return;
                }
                if (hosts.Count() == 0)
                {
                    ui.Text("実行中のホストがありません！\nwebからホストを開始してください");

                    return;
                }

                var lastSelectedHostId = BaruHDLIntegration._config?.GetValue(BaruHDLIntegration.LastSelectedHostIdKey);
                var defaultHostIndex = 0;
                if (!string.IsNullOrEmpty(lastSelectedHostId))
                {
                    defaultHostIndex = hosts.FindIndex(h => h.Id == lastSelectedHostId) switch
                    {
                        -1 => 0,
                        var index => index
                    };
                }
                var selectedHostIndexField = ui.HorizontalElementWithLabel("ホスト", 0.4f, () =>
                {
                    var hostLabels = hosts.Select(h => $"{h.Name}({h.Id.Substring(0, 6)})").ToList();
                    return BuildArrowSelector(rootSlot, ui, hostLabels, defaultHostIndex);
                });

                var nameField = ui.HorizontalElementWithLabel("World.Config.Name".AsLocaleKey(), 0.4f, () => ui.TextField());
                nameField.TargetString = worldOrb.WorldName;

                var accessLevelField = rootSlot.AttachComponent<ValueField<SessionAccessLevel>>();
                ui.Text("World.Config.AccessLevelHeader".AsLocaleKey("<b>{0}</b>"), bestFit: true, null);
                SessionControlDialog.GenerateAccessLevelUI(ui, accessLevelField.Value);

                var allowUsersField = ui.HorizontalElementWithLabel("現在のセッションのユーザに参加許可", 0.4f, () => ui.Checkbox(true));
                allowUsersField.IsChecked = BaruHDLIntegration._config?.GetValue(BaruHDLIntegration.LastCheckedAllowUsersKey) ?? false;

                var keepRolesField = ui.HorizontalElementWithLabel("現在のセッションのユーザ権限を維持する", 0.4f, () => ui.Checkbox(true));
                keepRolesField.IsChecked = BaruHDLIntegration._config?.GetValue(BaruHDLIntegration.LastCheckedKeepRolesKey) ?? false;

                var startBtn = ui.Button("World.Actions.StartSession".AsLocaleKey("<b>{0}</b>"));
                startBtn.LocalPressed += async (IButton button, ButtonEventData eventData) =>
                {
                    startBtn.Enabled = false;
                    startBtn.LabelText = "Starting...";

                    if (BaruHDLIntegration._config != null)
                    {
                        BaruHDLIntegration._config.Set(BaruHDLIntegration.LastSelectedHostIdKey, hosts[selectedHostIndexField.Value.Value].Id);
                        BaruHDLIntegration._config.Set(BaruHDLIntegration.LastCheckedAllowUsersKey, allowUsersField.IsChecked);
                        BaruHDLIntegration._config.Set(BaruHDLIntegration.LastCheckedKeepRolesKey, keepRolesField.IsChecked);
                    }

                    try
                    {
                        var host = hosts[selectedHostIndexField.Value.Value];
                        var parameters = new Headless.V1.WorldStartupParameters
                        {
                            Name = $"{nameField.TargetString}",
                            AccessLevel = accessLevelField.Value.Value switch
                            {
                                SessionAccessLevel.Private => AccessLevel.Private,
                                SessionAccessLevel.LAN => AccessLevel.Lan,
                                SessionAccessLevel.Contacts => AccessLevel.Contacts,
                                SessionAccessLevel.ContactsPlus => AccessLevel.ContactsPlus,
                                SessionAccessLevel.RegisteredUsers => AccessLevel.RegisteredUsers,
                                SessionAccessLevel.Anyone => AccessLevel.Anyone,
                                _ => throw new Exception("Invalid Access Level")
                            },
                            LoadWorldUrl = worldOrb.URL.ToString(),
                            JoinAllowedUserIds = {
                                allowUsersField.IsChecked
                                ? worldOrb.World.AllUsers.Select(u => u.UserID).Where(id => id != null).ToList()
                                : Enumerable.Empty<string>()
                            },
                            DefaultUserRoles = {
                                keepRolesField.IsChecked
                                ? worldOrb.World.AllUsers.Select(u => new DefaultUserRole { UserName = u.UserName, Role = u.Role.RoleName.Value }).ToList()
                                : Enumerable.Empty<DefaultUserRole>()
                            },
                        };
                        var startReq = new Hdlctrl.V1.StartWorldRequest
                        {
                            HostId = host.Id,
                            Parameters = parameters,
                            Memo = "Started by BaruHDLIntegration",
                        };

                        ResoniteMod.Msg($"Starting world {worldOrb.WorldName}({worldOrb.URL}) on {host.Name}({host.Id})");
                        var session = await client.StartWorld(startReq);

                        ResoniteMod.Msg($"Started {session.Name}");
                        NotificationMessage.SpawnTextMessage($"Started {session.Name}", color: new colorX(0.2f, 1f, 0.3f));
                        var sessionUris = session.CurrentState.ConnectUris.Select(s => new Uri(s)).ToList();
                        worldOrb.RunSynchronously(() =>
                        {
                            worldOrb.ActiveSessionURLs = sessionUris.Count() == 0 ? new List<Uri> { new Uri($"ressession:///{session.Id}") } : sessionUris;
                            rootSlot.Destroy();
                        });
                    }
                    catch (Exception ex)
                    {
                        ResoniteMod.Error($"Failed to start world: {ex}");
                        worldOrb.RunSynchronously(() =>
                        {
                            ui.Text($"Failed to start world: {ex}");

                            startBtn.Enabled = true;
                            startBtn.LabelText = "World.Actions.StartSession".AsLocaleKey("<b>{0}</b>").format;
                        });
                        return;
                    };
                };
            });
        }

        private static ValueField<int> BuildArrowSelector(Slot slot, UIBuilder ui, IList<string> labels, int defaultIndex = 0)
        {
            var field = slot.AttachComponent<ValueField<int>>();
            ui.HorizontalLayout(4f);

            ui.Style.FlexibleWidth = -1f;
            ui.Style.MinWidth = 24f;
            var prevBtn = ui.Button("<<");

            ui.Style.FlexibleWidth = 100f;
            ui.Style.MinWidth = -1f;
            var centerBtn = ui.Button();
            centerBtn.LabelText = labels[defaultIndex];

            ui.Style.FlexibleWidth = -1f;
            ui.Style.MinWidth = 24f;
            var nextBtn = ui.Button(">>");

            prevBtn.LocalPressed += (IButton button, ButtonEventData eventData) =>
            {
                field.Value.Value = (field.Value.Value - 1 + labels.Count()) % labels.Count();
                centerBtn.LabelText = labels[field.Value.Value];
            };
            nextBtn.LocalPressed += (IButton button, ButtonEventData eventData) =>
            {
                field.Value.Value = (field.Value.Value + 1) % labels.Count();
                centerBtn.LabelText = labels[field.Value.Value];
            };

            ui.NestOut();

            return field;
        }
    }
}
