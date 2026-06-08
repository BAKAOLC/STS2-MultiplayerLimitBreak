using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using STS2MultiplayerLimitBreak.Settings;
using STS2RitsuLib.Patching.Core;
using STS2RitsuLib.Patching.Models;
using STS2RitsuLib.Utils.HarmonyIl;

namespace STS2MultiplayerLimitBreak.Layout
{
    internal static class RestSiteRoomLayoutPatches
    {
        private static readonly Vector2 LeftExtraFrontOffset = new(-250f, 35f);

        private static readonly Vector2 LeftExtraBackOffset = new(-240f, -20f);

        private static readonly Vector2 RightExtraFrontOffset = new(250f, 35f);

        private static readonly Vector2 RightExtraBackOffset = new(240f, -20f);

        private static readonly Vector2 LogXOffsetLeft = new(-250f, 0f);

        private static readonly Vector2 LogXOffsetRight = new(250f, 0f);

        private static readonly Vector2 ExtraSeatStep = new(70f, -45f);

        private static readonly MethodInfo? CharacterContainerGetter =
            AccessTools.PropertyGetter(typeof(List<Control>), "Item");

        private static readonly MethodInfo SafeContainerGetter =
            AccessTools.Method(typeof(RestSiteRoomLayoutPatches), nameof(GetContainerSafe))
            ?? throw new InvalidOperationException("Rest site safe container helper was not found.");

        public static void AddTo(ModPatcher patcher)
        {
            patcher.RegisterPatch<ReadyPatch>();
        }

        private static IEnumerable<CodeInstruction> ReplaceContainerIndexer(IEnumerable<CodeInstruction> instructions)
        {
            var getter = CharacterContainerGetter
                         ?? throw new InvalidOperationException("List<Control>.Item getter was not found.");
            var rewriter = HarmonyIlRewriter.From(instructions);
            var report = rewriter.RedirectCalls(
                "mlb_rest_site_container_indexer",
                method => method == getter ? SafeContainerGetter : null);

            report.RequireSucceeded();
            return rewriter.InstructionsChecked("mlb_rest_site_container_indexer");
        }

        private static Control GetContainerSafe(List<Control> containers, int index)
        {
            if (containers.Count == 0)
                throw new InvalidOperationException("No rest site character containers were found.");

            if (RuntimeMultiplayerSettings.LimitBreakEnabled) EnsureContainers(containers, index + 1);

            return containers[NormalizeWrappedIndex(index, containers.Count)];
        }

        private static int NormalizeWrappedIndex(int index, int count)
        {
            var normalized = index % count;
            return normalized >= 0 ? normalized : normalized + count;
        }

        private static void EnsureContainers(List<Control> containers, int requiredCount)
        {
            if (requiredCount <= containers.Count) return;

            var parent = containers[0].GetParent<Control>();
            if (parent == null) return;

            EnsureExtraLogs(parent);
            var templateCount = containers.Count;
            while (containers.Count < requiredCount)
            {
                var count = containers.Count;
                var source = containers[count % templateCount];
                var container = source.Duplicate() as Control ?? new Control();
                RemoveAllChildren(container);
                container.Name = $"Character_Auto_{count + 1}";
                container.Position = GetExtraContainerPosition(containers, count);
                parent.AddChild(container);
                containers.Add(container);
            }
        }

        private static void RemoveAllChildren(Node node)
        {
            for (var i = node.GetChildCount() - 1; i >= 0; i--)
            {
                var child = node.GetChild(i);
                node.RemoveChild(child);
                child.QueueFree();
            }
        }

        private static Vector2 GetExtraContainerPosition(List<Control> containers, int index)
        {
            if (containers.Count < Const.VanillaPlayerLimit) return containers[^1].Position;

            if (index < Const.VanillaPlayerLimit) return containers[index].Position;

            var extraSeatIndex = index - Const.VanillaPlayerLimit;
            var isLeftSide = extraSeatIndex % 2 == 0;
            var depthLevel = extraSeatIndex / 2;
            var frontSeatPosition = isLeftSide
                ? containers[0].Position + LeftExtraFrontOffset
                : containers[1].Position + RightExtraFrontOffset;
            var backSeatPosition = isLeftSide
                ? containers[2].Position + LeftExtraBackOffset
                : containers[3].Position + RightExtraBackOffset;

            if (depthLevel == 0) return frontSeatPosition;

            if (depthLevel == 1) return backSeatPosition;

            var extraDepth = depthLevel - 1;
            Vector2 extraOffset = new((isLeftSide ? -1f : 1f) * ExtraSeatStep.X * extraDepth,
                ExtraSeatStep.Y * extraDepth);
            return backSeatPosition + extraOffset;
        }

        private static void EnsureExtraLogs(Control parent)
        {
            var background = parent.GetChildCount() > 0 ? parent.GetChild(0) : null;
            if (background == null || background.GetNodeOrNull<Node>("AutoExtraLogsMarker") != null) return;

            Node marker = new()
            {
                Name = "AutoExtraLogsMarker",
            };
            background.AddChild(marker);

            var leftLogOk = DuplicateShiftedNode(background, "RestSiteLLog", LogXOffsetLeft, "AutoL");
            var rightLogOk = DuplicateShiftedNode(background, "RestSiteRLog", LogXOffsetRight, "AutoR");
            var leftLogLayer2Ok =
                DuplicateShiftedNode(background, "RestSiteLighting/RestSiteLLog2", LogXOffsetLeft, "AutoL");
            var rightLogLayer2Ok =
                DuplicateShiftedNode(background, "RestSiteLighting/RestSiteRLog2", LogXOffsetRight, "AutoR");
            if (!leftLogOk && !rightLogOk && !leftLogLayer2Ok && !rightLogLayer2Ok)
                Log.Warn("No rest site log nodes were found for extra-player layout duplication.");
        }

        private static bool DuplicateShiftedNode(Node root, string nodePath, Vector2 offset, string suffix)
        {
            var node = root.GetNodeOrNull<Node>(nodePath);
            if (node == null)
            {
                Log.Warn($"Rest site node not found: {nodePath}");
                return false;
            }

            var parent = node.GetParent();
            if (parent == null)
            {
                Log.Warn($"Rest site node has no parent: {nodePath}");
                return false;
            }

            var duplicated = node.Duplicate();
            duplicated.Name = $"{node.Name}_{suffix}";
            parent.AddChild(duplicated);

            if (node is Control control && duplicated is Control duplicatedControl)
                duplicatedControl.Position = control.Position + offset;

            if (node is Node2D node2D && duplicated is Node2D duplicatedNode2D)
                duplicatedNode2D.Position = node2D.Position + offset;

            return true;
        }

        private sealed class ReadyPatch : IPatchMethod
        {
            public static string PatchId => "mlb_rest_site_room_character_container_layout";

            public static string Description => "Create enough rest site character containers for extended multiplayer";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(NRestSiteRoom), nameof(NRestSiteRoom._Ready)),
                ];
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return ReplaceContainerIndexer(instructions);
            }
        }
    }
}
