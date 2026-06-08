using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Runs;
using STS2MultiplayerLimitBreak.Settings;
using STS2RitsuLib.Patching.Core;
using STS2RitsuLib.Patching.Models;

namespace STS2MultiplayerLimitBreak.Layout
{
    internal static class TreasureRoomRelicLayoutPatches
    {
        private const float FallbackHolderXStep = 220f;

        private const float MinHolderXStep = 190f;

        private const float MinHolderYStep = 120f;

        private static readonly FieldInfo? HoldersInUseField =
            AccessTools.Field(typeof(NTreasureRoomRelicCollection), "_holdersInUse");

        private static readonly FieldInfo? MultiplayerHoldersField =
            AccessTools.Field(typeof(NTreasureRoomRelicCollection), "_multiplayerHolders");

        private static readonly FieldInfo? RunStateField =
            AccessTools.Field(typeof(NTreasureRoomRelicCollection), "_runState");

        public static void AddTo(ModPatcher patcher)
        {
            patcher.RegisterPatch<InitializeRelicsPatch>();
            patcher.RegisterPatch<DefaultFocusedControlPatch>();
        }

        private static void EnsureHolderCount(NTreasureRoomRelicCollection collection)
        {
            if (!RuntimeMultiplayerSettings.LimitBreakEnabled) return;

            var holdersInUse = GetHoldersInUse(collection);
            holdersInUse?.Clear();

            var multiplayerHolders = GetMultiplayerHolders(collection);
            if (multiplayerHolders != null && multiplayerHolders.Count > Const.VanillaPlayerLimit)
                for (var i = multiplayerHolders.Count - 1; i >= Const.VanillaPlayerLimit; i--)
                {
                    var holder = multiplayerHolders[i];
                    multiplayerHolders.RemoveAt(i);
                    holder.QueueFree();
                }

            var currentRelics = RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics;
            if (multiplayerHolders == null
                || currentRelics == null
                || currentRelics.Count <= multiplayerHolders.Count
                || multiplayerHolders.Count == 0)
                return;

            var template = multiplayerHolders[^1];
            var scene = string.IsNullOrEmpty(template.SceneFilePath)
                ? null
                : PreloadManager.Cache.GetScene(template.SceneFilePath);
            var parent = template.GetParent();

            for (var i = multiplayerHolders.Count; i < currentRelics.Count; i++)
            {
                var holder = scene?.Instantiate<NTreasureRoomRelicHolder>()
                             ?? template.Duplicate() as NTreasureRoomRelicHolder;
                if (holder == null) continue;

                holder.Name = $"AutoHolder_{i + 1}";
                holder.Visible = false;
                parent.AddChild(holder);
                multiplayerHolders.Add(holder);
            }
        }

        private static void RepositionHolders(NTreasureRoomRelicCollection collection)
        {
            if (!RuntimeMultiplayerSettings.LimitBreakEnabled) return;

            var holdersInUse = GetHoldersInUse(collection);
            if (holdersInUse == null || holdersInUse.Count <= Const.VanillaPlayerLimit) return;

            var minX = float.MaxValue;
            var maxX = float.MinValue;
            var topY = float.MaxValue;
            var bottomY = float.MinValue;
            for (var i = 0; i < Const.VanillaPlayerLimit; i++)
            {
                var position = holdersInUse[i].Position;
                minX = Math.Min(minX, position.X);
                maxX = Math.Max(maxX, position.X);
                topY = Math.Min(topY, position.Y);
                bottomY = Math.Max(bottomY, position.Y);
            }

            var holderCount = holdersInUse.Count;
            var maxColumns = holderCount >= 8
                ? Const.VanillaPlayerLimit
                : Math.Min(Const.VanillaPlayerLimit, holderCount);
            maxColumns = Math.Max(2, maxColumns);
            var rowCount = (int)Math.Ceiling(holderCount / (float)maxColumns);
            var centerX = (minX + maxX) * 0.5f;
            var centerY = (topY + bottomY) * 0.5f;
            var xStep = (maxX - minX) / Math.Max(1, maxColumns - 1);
            xStep = xStep > 0f ? Math.Max(MinHolderXStep, xStep) : FallbackHolderXStep;
            var yStep = Math.Max(MinHolderYStep, Math.Abs(bottomY - topY));
            var startIndex = 0;

            for (var row = 0; row < rowCount; row++)
            {
                var count = Math.Min(maxColumns, holderCount - startIndex);
                var y = centerY + (row - (rowCount - 1) * 0.5f) * yStep;
                LayoutRow(holdersInUse, startIndex, count, y, centerX, xStep);
                startIndex += count;
            }
        }

        private static void LayoutRow(List<NTreasureRoomRelicHolder> holders, int startIndex, int count, float y,
            float centerX, float xStep)
        {
            var startX = centerX - (count - 1) * xStep * 0.5f;
            for (var i = 0; i < count; i++) holders[startIndex + i].Position = new(startX + i * xStep, y);
        }

        private static bool TryGetDefaultFocusedControl(NTreasureRoomRelicCollection collection, ref Control result)
        {
            var holdersInUse = GetHoldersInUse(collection);
            if (holdersInUse == null || holdersInUse.Count == 0) return true;

            var runState = GetRunState(collection);
            var localPlayer = runState != null ? LocalContext.GetMe(runState.Players) : null;
            var playerSlotIndex =
                localPlayer != null && runState != null ? runState.GetPlayerSlotIndex(localPlayer) : 0;
            playerSlotIndex = Math.Clamp(playerSlotIndex, 0, holdersInUse.Count - 1);
            result = holdersInUse[playerSlotIndex];
            return false;
        }

        private static List<NTreasureRoomRelicHolder>? GetHoldersInUse(NTreasureRoomRelicCollection collection)
        {
            return HoldersInUseField?.GetValue(collection) as List<NTreasureRoomRelicHolder>;
        }

        private static List<NTreasureRoomRelicHolder>? GetMultiplayerHolders(NTreasureRoomRelicCollection collection)
        {
            return MultiplayerHoldersField?.GetValue(collection) as List<NTreasureRoomRelicHolder>;
        }

        private static IRunState? GetRunState(NTreasureRoomRelicCollection collection)
        {
            return RunStateField?.GetValue(collection) as IRunState;
        }

        private sealed class InitializeRelicsPatch : IPatchMethod
        {
            public static string PatchId => "mlb_treasure_room_relic_holder_layout";

            public static string Description =>
                "Create and reposition treasure room relic holders for extended multiplayer";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(NTreasureRoomRelicCollection),
                        nameof(NTreasureRoomRelicCollection.InitializeRelics)),
                ];
            }

            private static void Prefix(NTreasureRoomRelicCollection __instance)
            {
                EnsureHolderCount(__instance);
            }

            private static void Postfix(NTreasureRoomRelicCollection __instance)
            {
                RepositionHolders(__instance);
            }
        }

        private sealed class DefaultFocusedControlPatch : IPatchMethod
        {
            public static string PatchId => "mlb_treasure_room_relic_default_focus";

            public static string Description => "Focus the local player's treasure room relic holder";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(NTreasureRoomRelicCollection), "get_DefaultFocusedControl"),
                ];
            }

            private static bool Prefix(NTreasureRoomRelicCollection __instance, ref Control __result)
            {
                return TryGetDefaultFocusedControl(__instance, ref __result);
            }
        }
    }
}
