using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Patching.Core;
using STS2RitsuLib.Patching.Models;

namespace STS2MultiplayerLimitBreak.Layout
{
    internal static class TreasureRoomRelicLayoutPatches
    {
        private const string HolderScenePath = "res://scenes/ui/treasure_relic_holder.tscn";

        private const float FallbackHolderXStep = 220f;

        private const float MinHolderXStep = 190f;

        private const float MinHolderYStep = 120f;

        private static readonly FieldInfo? HoldersInUseField =
            AccessTools.Field(typeof(NTreasureRoomRelicCollection), "_holdersInUse");

        private static readonly FieldInfo? MultiplayerHoldersField =
            AccessTools.Field(typeof(NTreasureRoomRelicCollection), "_multiplayerHolders");

        private static readonly FieldInfo? HolderLayoutsField =
            AccessTools.Field(typeof(NTreasureRoomRelicCollection), "_holderLayouts");

        private static readonly FieldInfo? RunStateField =
            AccessTools.Field(typeof(NTreasureRoomRelicCollection), "_runState");

        public static void AddTo(ModPatcher patcher)
        {
            patcher.RegisterPatch<InitializeRelicsPatch>();
            patcher.RegisterPatch<DefaultFocusedControlPatch>();
        }

        private static void EnsureHolderCount(NTreasureRoomRelicCollection collection)
        {
            if (HolderLayoutsField?.GetValue(collection) is List<Control> layouts)
            {
                EnsureIndexedLayouts(layouts);
                return;
            }

            var holdersInUse = GetHoldersInUse(collection);
            holdersInUse?.Clear();

            var multiplayerHolders = GetMultiplayerHolders(collection);
            if (multiplayerHolders is { Count: > Const.VanillaPlayerLimit })
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
            var scene = PreloadManager.Cache.GetScene(HolderScenePath);
            var parent = template.GetParent();

            for (var i = multiplayerHolders.Count; i < currentRelics.Count; i++)
            {
                var holder = scene.Instantiate<NTreasureRoomRelicHolder>();

                holder.Name = $"AutoHolder_{i + 1}";
                holder.Visible = false;
                parent.AddChild(holder);
                multiplayerHolders.Add(holder);
            }
        }

        private static void EnsureIndexedLayouts(List<Control> layouts)
        {
            var currentRelics = RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics;
            if (currentRelics == null || currentRelics.Count <= layouts.Count)
                return;

            var scene = PreloadManager.Cache.GetScene(HolderScenePath);
            var parent = layouts[0].GetParent();
            for (var layoutIndex = layouts.Count; layoutIndex < currentRelics.Count; layoutIndex++)
            {
                var layout = new Control
                {
                    Name = $"MLBLayout_{layoutIndex + 1}",
                    Visible = false,
                };
                parent.AddChild(layout);
                layout.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

                List<NTreasureRoomRelicHolder> holders = new(layoutIndex + 1);
                for (var holderIndex = 0; holderIndex <= layoutIndex; holderIndex++)
                {
                    var holder = scene.Instantiate<NTreasureRoomRelicHolder>();
                    holder.Name = $"AutoHolder_{holderIndex + 1}";
                    layout.AddChild(holder);
                    holders.Add(holder);
                }

                var columns = Math.Min(Const.VanillaPlayerLimit, holders.Count);
                var rowCount = (int)Math.Ceiling(holders.Count / (float)columns);
                var center = (layout.Size - holders[0].Size) * 0.5f;
                var yStep = Math.Max(MinHolderYStep, holders[0].Size.Y + 24f);
                for (var row = 0; row < rowCount; row++)
                {
                    var startIndex = row * columns;
                    var count = Math.Min(columns, holders.Count - startIndex);
                    var y = center.Y + (row - (rowCount - 1) * 0.5f) * yStep;
                    LayoutRow(holders, startIndex, count, y, center.X, FallbackHolderXStep);
                }

                layouts.Add(layout);
            }
        }

        private static void RepositionHolders(NTreasureRoomRelicCollection collection)
        {
            if (HolderLayoutsField != null)
                return;

            var holdersInUse = GetHoldersInUse(collection);
            if (holdersInUse is not { Count: > Const.VanillaPlayerLimit }) return;

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
            if (playerSlotIndex >= 0 && playerSlotIndex < holdersInUse.Count)
                return true;

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

        // ReSharper disable once ClassNeverInstantiated.Local
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

            // ReSharper disable once InconsistentNaming
            // ReSharper disable once UnusedMember.Local
            private static void Prefix(NTreasureRoomRelicCollection __instance)
            {
                EnsureHolderCount(__instance);
            }

            // ReSharper disable once InconsistentNaming
            // ReSharper disable once UnusedMember.Local
            private static void Postfix(NTreasureRoomRelicCollection __instance)
            {
                RepositionHolders(__instance);
            }
        }

        // ReSharper disable once ClassNeverInstantiated.Local
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

            // ReSharper disable InconsistentNaming
            // ReSharper disable once UnusedMember.Local
            private static bool Prefix(NTreasureRoomRelicCollection __instance, ref Control __result)
            {
                return TryGetDefaultFocusedControl(__instance, ref __result);
            }
            // ReSharper restore InconsistentNaming
        }
    }
}
