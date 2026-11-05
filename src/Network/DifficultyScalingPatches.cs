using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using STS2MultiplayerLimitBreak.Settings;
using STS2RitsuLib.Patching.Builders;
using STS2RitsuLib.Patching.Core;
using STS2RitsuLib.Patching.Models;
using STS2RitsuLib.Utils.HarmonyIl;

namespace STS2MultiplayerLimitBreak.Network
{
    internal static class DifficultyScalingPatches
    {
        private static readonly MethodInfo EffectivePlayerCountMethod =
            AccessTools.Method(typeof(RuntimeMultiplayerSettings),
                nameof(RuntimeMultiplayerSettings.GetEffectivePlayerCount))
            ?? throw new InvalidOperationException("Effective player-count helper was not found.");

        private static readonly MethodInfo? CombatStatePlayersGetter =
            AccessTools.PropertyGetter(typeof(ICombatState), nameof(ICombatState.Players));

        private static readonly Type MultiplayerScalingModelType =
            typeof(AbstractModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.MultiplayerScalingModel")
            ?? typeof(AbstractModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.Singleton.MultiplayerScalingModel")
            ?? throw new InvalidOperationException("Multiplayer scaling model was not found.");

        private static readonly FieldInfo? RunStateField =
            AccessTools.DeclaredField(MultiplayerScalingModelType, "_runState");

        private static readonly MethodInfo? RunStateGetter =
            AccessTools.DeclaredPropertyGetter(MultiplayerScalingModelType.BaseType, "RunState");

        private static readonly MethodInfo? RunStatePlayersGetter =
            AccessTools.PropertyGetter(typeof(RunState), nameof(RunState.Players));

        public static void AddTo(ModPatcher patcher)
        {
            patcher.RegisterPatch<ScaleMonsterHpPatch>();
            patcher.RegisterPatch<ModifyBlockScalingPatch>();
        }

        public static bool ApplyPowerScalingPatches(ModPatcher patcher)
        {
            var transpiler = DynamicPatchBuilder.FromMethod(
                typeof(DifficultyScalingPatches),
                nameof(PowerScalingTranspiler));
            var builder = new DynamicPatchBuilder("mlb_power_multiplayer_scaling");
            var parameterTypes = new[]
            {
                typeof(ICombatState),
                typeof(Creature),
                typeof(decimal),
                typeof(Creature),
                typeof(CardModel),
            };
            Type[] declaringTypes =
            [
                typeof(PowerModel),
                typeof(PlatingPower),
                typeof(BufferPower),
                typeof(SlipperyPower),
                typeof(SkittishPower),
                typeof(ArtifactPower),
            ];

            foreach (var declaringType in declaringTypes)
            {
                var method = AccessTools.DeclaredMethod(
                    declaringType,
                    nameof(PowerModel.GetScaledAmountForMultiplayer),
                    parameterTypes);
                if (method is not { IsAbstract: false })
                    continue;

                builder.Add(
                    method,
                    transpiler: transpiler,
                    description: "Apply host player-scaling multiplier to multiplayer power scaling");
            }

            return patcher.ApplyDynamic(builder, rollbackOnCriticalFailure: true);
        }

        private static List<CodeInstruction> PowerScalingTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return PatchCountAfterPlayersGetter(
                instructions,
                CombatStatePlayersGetter,
                "mlb_power_multiplayer_scaling_count");
        }

        private static List<CodeInstruction> BlockScalingTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            if (RunStateField == null && RunStateGetter == null)
                throw new InvalidOperationException("Multiplayer scaling run-state accessor was not found.");

            var playersGetter = RunStatePlayersGetter
                                ?? throw new InvalidOperationException("Run-state players getter was not found.");
            var rewriter = HarmonyIlRewriter.From(instructions);
            var report = rewriter.ReplaceEach(
                "mlb_block_multiplayer_scaling_count",
                (code, index) => index >= 2 && IsIntCountGetter(code[index])
                                            && HarmonyIl.IsCallTo(code[index - 1], playersGetter)
                                            && ((RunStateField != null && code[index - 2].LoadsField(RunStateField))
                                                || (RunStateGetter != null &&
                                                    HarmonyIl.IsCallTo(code[index - 2], RunStateGetter))),
                (code, index) => [code[index].Clone(), HarmonyIl.Call(EffectivePlayerCountMethod)],
                HasEffectivePlayerCountCall);

            report.RequireSucceeded();
            report.RequireExactly(1);
            return rewriter.InstructionsChecked("mlb_block_multiplayer_scaling_count");
        }

        private static List<CodeInstruction> PatchCountAfterPlayersGetter(
            IEnumerable<CodeInstruction> instructions,
            MethodInfo? playersGetter,
            string operation)
        {
            var resolvedPlayersGetter = playersGetter
                                        ?? throw new InvalidOperationException(
                                            $"{operation}: players getter was not found.");
            var rewriter = HarmonyIlRewriter.From(instructions);
            var awaitingCount = false;
            var report = rewriter.ReplaceEach(
                operation,
                (code, index) =>
                {
                    var instruction = code[index];
                    if (HarmonyIl.IsCallTo(instruction, resolvedPlayersGetter))
                    {
                        awaitingCount = true;
                        return false;
                    }

                    if (!awaitingCount)
                        return false;

                    var hasCount = IsIntCountGetter(instruction);
                    awaitingCount = !hasCount && !IsControlFlowBoundary(instruction);
                    return hasCount;
                },
                (code, index) => [code[index].Clone(), HarmonyIl.Call(EffectivePlayerCountMethod)],
                HasEffectivePlayerCountCall);

            report.RequireSucceeded();
            report.RequireExactly(1);
            return rewriter.InstructionsChecked(operation);
        }

        private static bool IsIntCountGetter(CodeInstruction instruction)
        {
            return (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                   && instruction.operand is MethodInfo { Name: "get_Count", ReturnType: { } returnType }
                   && returnType == typeof(int);
        }

        private static bool IsControlFlowBoundary(CodeInstruction instruction)
        {
            return instruction.opcode.FlowControl is FlowControl.Branch
                or FlowControl.Cond_Branch
                or FlowControl.Return
                or FlowControl.Throw;
        }

        private static bool HasEffectivePlayerCountCall(IReadOnlyList<CodeInstruction> code)
        {
            return code.Any(instruction => HarmonyIl.IsCallTo(instruction, EffectivePlayerCountMethod));
        }

        internal sealed class ScaleMonsterHpPatch : IPatchMethod
        {
            public static string PatchId => "mlb_scale_monster_hp_player_count";

            public static string Description => "Apply host player-scaling multiplier to monster HP scaling";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(Creature), nameof(Creature.ScaleMonsterHpForMultiplayer)),
                ];
            }

            public static void Prefix(ref int playerCount)
            {
                playerCount = RuntimeMultiplayerSettings.GetEffectivePlayerCount(playerCount);
            }
        }

        internal sealed class ModifyBlockScalingPatch : IPatchMethod
        {
            public static string PatchId => "mlb_modify_block_player_count";

            public static string Description => "Apply host player-scaling multiplier to block scaling";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(MultiplayerScalingModelType, nameof(AbstractModel.ModifyBlockMultiplicative)),
                ];
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return BlockScalingTranspiler(instructions);
            }
        }
    }
}
