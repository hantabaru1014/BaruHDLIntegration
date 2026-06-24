using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BaruHDLIntegration.Hdl;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using Hdlctrl.V1;
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
            // FrooxEngine.WorldOrb+<>c__DisplayClass156_0.<ToggleContextMenu>b__0 を探したいので、まず c__DisplayClass156_0 をあてる
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
                    OpenStartFormForOrb(worldOrb);
                }
                catch (Exception ex)
                {
                    ResoniteMod.Error($"Failed to open world start form: {ex}");
                    return;
                }

                menu.RunSynchronously(() =>
                {
                    menu.LocalUser.CloseContextMenu(worldOrb);
                });
            };
        }

        static void OpenStartFormForOrb(WorldOrb worldOrb)
        {
            var ctx = new StartWorldFormContext
            {
                Title = "Start World by Headless",
                DefaultName = worldOrb.WorldName,
                LoadWorldUrl = worldOrb.URL?.ToString() ?? "",
                AvailableUserIds = worldOrb.World.AllUsers.Select(u => u.UserID).Where(id => id != null).ToList(),
                AvailableUserRoles = worldOrb.World.AllUsers
                    .Select(u => new Headless.Rpc.DefaultUserRole { UserName = u.UserName, Role = u.Role.RoleName.Value })
                    .ToList(),
                OnStarted = response =>
                {
                    var session = response.OpenedSession;
                    if (session == null) return;
                    var sessionUris = session.CurrentState?.ConnectUris?.Select(s => new Uri(s)).ToList() ?? new List<Uri>();
                    worldOrb.RunSynchronously(() =>
                    {
                        worldOrb.ActiveSessionURLs = sessionUris.Count() == 0
                            ? new List<Uri> { new Uri($"ressession:///{session.Id}") }
                            : sessionUris;
                    });
                },
            };
            HdlStartWorldForm.Open(worldOrb.World, ctx);
        }
    }
}
