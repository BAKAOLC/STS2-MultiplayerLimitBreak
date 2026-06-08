using Godot;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using STS2MultiplayerLimitBreak.Settings;
using STS2RitsuLib.Patching.Core;
using STS2RitsuLib.Patching.Models;

namespace STS2MultiplayerLimitBreak.Layout
{
    internal static class MerchantRoomLayoutPatches
    {
        private const float ForwardShiftX = 160f;

        private const float ForwardShiftY = 35f;

        private const float RowStartOffsetX = -110f;

        private const float RowStepY = -40f;

        private const float ColumnStepX = -230f;

        public static void AddTo(ModPatcher patcher)
        {
            patcher.RegisterPatch<AfterRoomLoadedPatch>();
        }

        private static void Reposition(IReadOnlyList<NMerchantCharacter> visuals)
        {
            if (!RuntimeMultiplayerSettings.LimitBreakEnabled || visuals.Count <= Const.VanillaPlayerLimit) return;

            var rowCount = visuals.Count <= Const.VanillaPlayerLimit * 2
                ? 2
                : Mathf.CeilToInt((float)visuals.Count / Const.VanillaPlayerLimit);
            var columnCount = Mathf.CeilToInt((float)visuals.Count / rowCount);
            var visualIndex = 0;

            for (var row = 0; row < rowCount; row++)
            {
                var x = ForwardShiftX + RowStartOffsetX * row;
                var y = ForwardShiftY + RowStepY * row;
                for (var column = 0; column < columnCount && visualIndex < visuals.Count; column++)
                {
                    visuals[visualIndex].Position = new(x, y);
                    x += ColumnStepX;
                    visualIndex++;
                }
            }
        }

        private sealed class AfterRoomLoadedPatch : IPatchMethod
        {
            public static string PatchId => "mlb_merchant_room_player_layout";

            public static string Description => "Reposition merchant room multiplayer characters";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(NMerchantRoom), "AfterRoomIsLoaded"),
                ];
            }

            private static void Postfix(NMerchantRoom __instance)
            {
                Reposition(__instance.PlayerVisuals);
            }
        }
    }
}
