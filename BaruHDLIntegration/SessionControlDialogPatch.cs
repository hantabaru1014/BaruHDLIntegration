using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using ResoniteModLoader;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace BaruHDLIntegration
{
    [HarmonyPatch(typeof(SessionControlDialog))]
    static class SessionControlDialogPatch
    {
        private static RectTransform? _panelRect;

        /// <summary>
        /// セッションタブの設定タブの左側のボタン類の最後にHDL操作のボタンを追加する
        /// </summary>
        [HarmonyPatch("GenerateUi")]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> GenerateUi_Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var foundSaveCopyLdstr = false;
            var patched = false;
            var popStyleMethod = AccessTools.Method(typeof(UIBuilder), nameof(UIBuilder.PopStyle));

            foreach (var instruction in instructions)
            {
                if (instruction.Is(OpCodes.Ldstr, "World.Actions.SaveCopy"))
                {
                    foundSaveCopyLdstr = true;
                }
                if (!patched && foundSaveCopyLdstr && instruction.Calls(popStyleMethod))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SessionControlDialogPatch), nameof(InsertPanel)));
                    yield return instruction;
                    patched = true;
                }
                else
                {
                    yield return instruction;
                }
            }
        }

        static UIBuilder InsertPanel(UIBuilder ui)
        {
            _panelRect = ui.Panel();
            ui.NestOut();

            return ui;
        }

        [HarmonyPatch("UpdateValueSyncs")]
        [HarmonyPostfix]
        public static void UpdateValueSyncs_Postfix(World world)
        {
            if (_panelRect is null) return;

            _panelRect.Slot.RunSynchronously(() =>
            {
                _panelRect.Slot.DestroyChildren();
            });

            world.Coroutines.StartBackgroundTask(async () =>
            {
                var client = BaruHDLIntegration.GetClient();
                ResoniteMod.Msg($"Getting session details for world: {world.Name}({world.SessionId})");
                try
                {
                    var session = await client.GetSession(world.SessionId);
                    _panelRect.Slot.RunSynchronously(() =>
                    {
                        BuildPanel(_panelRect, session);
                    });
                }
                catch (Exception ex)
                {
                    ResoniteMod.Warn($"Failed to get session details: {ex}");
                }
            });
        }

        static void BuildPanel(RectTransform panelRect, Hdlctrl.V1.Session session)
        {
            panelRect.Slot.DestroyChildren();
            var ui = new UIBuilder(panelRect);
            RadiantUI_Constants.SetupDefaultStyle(ui);

            ui.VerticalLayout(4f, 0f);
            ui.FitContent(SizeFit.Disabled, SizeFit.PreferredSize);
            ui.Style.MinHeight = 24f;
            ui.Style.PreferredHeight = 24f;

            ui.Spacer(30f);
            ui.Text("ヘッドレス操作:", bestFit: true);

            var saveButton = ui.Button(OfficialAssets.Graphics.Icons.Dash.SaveWorld, "World.Actions.Save".AsLocaleKey());
            saveButton.LocalPressed += async (IButton button, ButtonEventData eventData) =>
            {
                var client = BaruHDLIntegration.GetClient();
                saveButton.Slot.RunSynchronously(() =>
                {
                    saveButton.Enabled = false;
                    saveButton.LabelText = "Saving...";
                });
                await client.SaveWorld(session.HostId, session.Id);
                saveButton.Slot.RunSynchronously(() =>
                {
                    saveButton.Enabled = true;
                    saveButton.LabelText = "World.Actions.Save".AsLocaleKey().format;
                });
            };
            saveButton.Enabled = session.CurrentState.CanSave;

            var stopButton = ui.Button(OfficialAssets.Graphics.Icons.Dash.CloseWorld, "World.Actions.Close".AsLocaleKey());
            stopButton.LocalPressed += async (IButton button, ButtonEventData eventData) =>
            {
                var client = BaruHDLIntegration.GetClient();
                stopButton.Slot.RunSynchronously(() =>
                {
                    stopButton.Enabled = false;
                    stopButton.LabelText = "Stopping...";
                });
                await client.StopWorld(session.HostId, session.Id);
                stopButton.Slot.RunSynchronously(() =>
                {
                    stopButton.Enabled = true;
                    stopButton.LabelText = "World.Actions.Close".AsLocaleKey().format;
                });
            };

            if (session.CurrentState.LastSavedAt != null)
            {
                ui.Text($"Last Saved: {session.CurrentState.LastSavedAt.ToDateTimeOffset():yyyy/MM/dd HH:mm:ss}");
            }
        }
    }
}
