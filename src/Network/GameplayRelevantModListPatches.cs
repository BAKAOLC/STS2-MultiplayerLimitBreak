using MegaCrit.Sts2.Core.Modding;
using STS2MultiplayerLimitBreak.Settings;
using STS2RitsuLib.Patching.Core;
using STS2RitsuLib.Patching.Models;

namespace STS2MultiplayerLimitBreak.Network
{
    internal static class GameplayRelevantModListPatches
    {
        public static void AddTo(ModPatcher patcher)
        {
            patcher.RegisterPatch<GameplayRelevantModNameListPatch>();
        }

        private sealed class GameplayRelevantModNameListPatch : IPatchMethod
        {
            public static string PatchId => "mlb_hide_from_gameplay_mod_list_when_disabled";

            public static string Description =>
                "Hide this mod from vanilla multiplayer mod checks while limit break is disabled";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(ModManager), nameof(ModManager.GetGameplayRelevantModNameList)),
                ];
            }

            private static void Postfix(ref List<string>? __result)
            {
                if (RuntimeMultiplayerSettings.LimitBreakEnabled || __result == null)
                    return;

                __result.RemoveAll(static mod => mod.StartsWith(Const.ModId + "-", StringComparison.Ordinal));
                if (__result.Count == 0)
                    __result = null;
            }
        }
    }
}
