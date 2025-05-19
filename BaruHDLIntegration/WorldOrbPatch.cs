using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
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
            var iconUri = OfficialAssets.Graphics.Icons.General.NewCustom;
            var item = menu.AddItem("ヘッドレスでセッション開始", iconUri, null);
            item.Button.LocalPressed += (IButton button, ButtonEventData eventData) =>
            {
                BuildWorldStartPanel(worldOrb);

                menu.RunSynchronously(() =>
                {
                    menu.LocalUser.CloseContextMenu(worldOrb);
                });
            };
        }

        private static void BuildWorldStartPanel(WorldOrb worldOrb)
        {
            worldOrb.RunSynchronously(() =>
            {
                var rootSlot = worldOrb.World.AddSlot("Session Start", persistent: false);
                rootSlot.PositionInFrontOfUser(float3.Backward);
                var ui = RadiantUI_Panel.SetupPanel(rootSlot, "Start World by Headless", new float2(800f, 640f));
                rootSlot.LocalScale *= 0.0005f;
                RadiantUI_Constants.SetupEditorStyle(ui);
                rootSlot.SetContainerTitle("Start World by Headless");
                ui.ScrollArea(null);
                ui.VerticalLayout(4f, 0f, null, null, null);
                ui.FitContent(SizeFit.Disabled, SizeFit.PreferredSize);
                ui.Style.MinHeight = 32f;
                ui.Style.PreferredHeight = 32f;

                var nameField = ui.HorizontalElementWithLabel("World.Config.Name".AsLocaleKey(), 0.4f, () => ui.TextField());
                nameField.TargetString = worldOrb.WorldName;
                
                var startBtn = ui.Button("World.Actions.StartSession".AsLocaleKey("<b>{0}</b>"));
                startBtn.LocalPressed += async (IButton button, ButtonEventData eventData) =>
                {
                    startBtn.Enabled = false;
                    startBtn.LabelText = "Starting...";

                    var client = BaruHDLIntegration.GetClient();
                    var host = (await client.ListHeadlessHost()).Where(h => h.Status == "HEADLESS_HOST_STATUS_RUNNING").First();
                    if (host == null)
                    {
                        ResoniteMod.Warn($"No Runnning Headless Hosts");
                        NotificationMessage.SpawnTextMessage("No Runnning Headless Hosts", color: new colorX(1f, 0.2f, 0.3f));

                        worldOrb.RunSynchronously(() =>
                        {
                            startBtn.Enabled = true;
                            startBtn.LabelText = "World.Actions.StartSession".AsLocaleKey("<b>{0}</b>").ToString();
                            rootSlot.Destroy();
                        });
                        return;
                    }
                    try
                    {
                        var startSettings = new WorldStartSettings
                        {
                            Link = worldOrb,
                            DefaultAccessLevel = SessionAccessLevel.ContactsPlus,
                            FetchedWorldName = nameField.TargetString,
                        };
                        ResoniteMod.Msg($"Starting world {startSettings.FetchedWorldName.ToString()}({startSettings.Link.URL}) on {host.Name}({host.AccountId})");
                        var startedId = await client.StartWorld(host.Id, startSettings);

                        ResoniteMod.Msg($"Started world {startedId} on {host.Name}");
                        NotificationMessage.SpawnTextMessage($"Started world {startedId} on {host.Name}", color: new colorX(0.2f, 1f, 0.3f));
                        worldOrb.RunSynchronously(() =>
                        {
                            worldOrb.ActiveSessionURLs = new List<Uri> { new Uri($"ressession:///{startedId}") };
                            rootSlot.Destroy();
                        });
                    }
                    catch (Exception ex)
                    {
                        ResoniteMod.Error($"Failed to start world: {ex}");
                        NotificationMessage.SpawnTextMessage($"Failed to start world: {ex}", color: new colorX(1f, 0.2f, 0.3f));

                        worldOrb.RunSynchronously(() =>
                        {
                            startBtn.Enabled = true;
                            startBtn.LabelText = "World.Actions.StartSession".AsLocaleKey("<b>{0}</b>").ToString();
                            rootSlot.Destroy();
                        });
                        return;
                    };
                };
            });
        }
    }
}
