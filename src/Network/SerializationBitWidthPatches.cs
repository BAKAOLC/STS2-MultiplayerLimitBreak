using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using STS2MultiplayerLimitBreak.Settings;
using STS2RitsuLib.Patching.Core;
using STS2RitsuLib.Patching.Models;
using STS2RitsuLib.Utils.HarmonyIl;

namespace STS2MultiplayerLimitBreak.Network
{
    internal static class SerializationBitWidthPatches
    {
        private static readonly MethodInfo? WriteIntWithBits =
            AccessTools.Method(typeof(PacketWriter), nameof(PacketWriter.WriteInt), [typeof(int), typeof(int)]);

        private static readonly MethodInfo? ReadIntWithBits =
            AccessTools.Method(typeof(PacketReader), nameof(PacketReader.ReadInt), [typeof(int)]);

        private static readonly MethodInfo? WriteListWithBits =
            typeof(PacketWriter).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method => method.Name == nameof(PacketWriter.WriteList)
                                          && method.IsGenericMethodDefinition
                                          && method.GetParameters().Length == 2
                                          && method.GetParameters()[1].ParameterType == typeof(int));

        private static readonly MethodInfo? ReadListWithBits =
            typeof(PacketReader).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method => method.Name == nameof(PacketReader.ReadList)
                                          && method.IsGenericMethodDefinition
                                          && method.GetParameters().Length == 1
                                          && method.GetParameters()[0].ParameterType == typeof(int));

        public static void AddTo(ModPatcher patcher)
        {
            patcher.RegisterPatch<LobbyPlayerSerializeSlotIdBitsPatch>();
            patcher.RegisterPatch<LobbyPlayerDeserializeSlotIdBitsPatch>();
            patcher.RegisterPatch<ClientLobbyJoinResponseSerializeListBitsPatch>();
            patcher.RegisterPatch<ClientLobbyJoinResponseDeserializeListBitsPatch>();
            patcher.RegisterPatch<LobbyBeginRunSerializeListBitsPatch>();
            patcher.RegisterPatch<LobbyBeginRunDeserializeListBitsPatch>();
        }

        private static IEnumerable<CodeInstruction> ReplaceBitWidthBeforeCall(
            IEnumerable<CodeInstruction> instructions,
            MethodInfo? targetMethod,
            int sourceBitWidth,
            int targetBitWidth,
            string operation)
        {
            var resolvedTargetMethod = targetMethod
                                       ?? throw new InvalidOperationException(
                                           $"{operation}: packet method was not found.");

            var rewriter = HarmonyIlRewriter.From(instructions);
            var selectorMethod = AccessTools.Method(
                                     typeof(SerializationBitWidthPatches),
                                     nameof(SelectBitWidth),
                                     [typeof(int), typeof(int)])
                                 ?? throw new InvalidOperationException(
                                     $"{operation}: bit-width selector method was not found.");

            var report = rewriter.ReplaceEach(
                operation,
                (code, index) => IsBitWidthArgumentForCall(code, index, resolvedTargetMethod, sourceBitWidth),
                (code, index) =>
                [
                    HarmonyIl.LdcI4(GetLoadedInt32(code[index], operation)),
                    HarmonyIl.LdcI4(targetBitWidth),
                    HarmonyIl.Call(selectorMethod),
                ],
                code => HasDynamicBitWidthArgumentForCall(code, resolvedTargetMethod, selectorMethod));

            report.RequireSucceeded();
            if (report.Applied > 0) report.RequireExactly(1);

            return rewriter.InstructionsChecked(operation);
        }

        private static int SelectBitWidth(int vanillaBitWidth, int extendedBitWidth)
        {
            return RuntimeMultiplayerSettings.LimitBreakEnabled ? extendedBitWidth : vanillaBitWidth;
        }

        private static bool HasDynamicBitWidthArgumentForCall(
            IReadOnlyList<CodeInstruction> instructions,
            MethodInfo targetMethod,
            MethodInfo selectorMethod)
        {
            for (var i = 0; i < instructions.Count; i++)
            {
                if (!HarmonyIl.IsCallTo(instructions[i], selectorMethod)) continue;

                var searchEnd = Math.Min(instructions.Count, i + 9);
                for (var j = i + 1; j < searchEnd; j++)
                    if (IsCallToTarget(instructions[j], targetMethod))
                        return true;
            }

            return false;
        }

        private static bool IsBitWidthArgumentForCall(
            IReadOnlyList<CodeInstruction> instructions,
            int loadIndex,
            MethodInfo targetMethod,
            int bitWidth)
        {
            if (!HarmonyIl.LoadsInt32(instructions[loadIndex], bitWidth)) return false;

            var searchEnd = Math.Min(instructions.Count, loadIndex + 9);
            for (var i = loadIndex + 1; i < searchEnd; i++)
            {
                var instruction = instructions[i];
                if (IsCallToTarget(instruction, targetMethod)) return true;

                if (instruction.opcode.FlowControl is FlowControl.Branch
                    or FlowControl.Cond_Branch
                    or FlowControl.Return
                    or FlowControl.Throw)
                    return false;
            }

            return false;
        }

        private static bool IsCallToTarget(CodeInstruction instruction, MethodInfo targetMethod)
        {
            return HarmonyIl.IsCallTo(instruction, targetMethod)
                   || HarmonyIl.IsCallToGenericDefinition(instruction, targetMethod);
        }

        private static int GetLoadedInt32(CodeInstruction instruction, string operation)
        {
            if (HarmonyIl.TryGetInt32(instruction, out var value)) return value;

            throw new InvalidOperationException($"{operation}: expected an int32 load instruction.");
        }

        private sealed class LobbyPlayerSerializeSlotIdBitsPatch : IPatchMethod
        {
            public static string PatchId => "mlb_lobby_player_serialize_slot_id_bits";

            public static string Description => "Raise serialized lobby player slot id width";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(LobbyPlayer), nameof(LobbyPlayer.Serialize)),
                ];
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return ReplaceBitWidthBeforeCall(instructions, WriteIntWithBits, Const.VanillaSlotIdBits,
                    Const.SlotIdBits,
                    PatchId);
            }
        }

        private sealed class LobbyPlayerDeserializeSlotIdBitsPatch : IPatchMethod
        {
            public static string PatchId => "mlb_lobby_player_deserialize_slot_id_bits";

            public static string Description => "Raise deserialized lobby player slot id width";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(LobbyPlayer), nameof(LobbyPlayer.Deserialize)),
                ];
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return ReplaceBitWidthBeforeCall(instructions, ReadIntWithBits, Const.VanillaSlotIdBits,
                    Const.SlotIdBits,
                    PatchId);
            }
        }

        private sealed class ClientLobbyJoinResponseSerializeListBitsPatch : IPatchMethod
        {
            public static string PatchId => "mlb_client_lobby_join_response_serialize_list_bits";

            public static string Description => "Raise serialized client lobby join response player list width";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(ClientLobbyJoinResponseMessage),
                        nameof(ClientLobbyJoinResponseMessage.Serialize)),
                ];
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return ReplaceBitWidthBeforeCall(instructions, WriteListWithBits, Const.VanillaLobbyListLengthBits,
                    Const.LobbyListLengthBits, PatchId);
            }
        }

        private sealed class ClientLobbyJoinResponseDeserializeListBitsPatch : IPatchMethod
        {
            public static string PatchId => "mlb_client_lobby_join_response_deserialize_list_bits";

            public static string Description => "Raise deserialized client lobby join response player list width";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(ClientLobbyJoinResponseMessage),
                        nameof(ClientLobbyJoinResponseMessage.Deserialize)),
                ];
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return ReplaceBitWidthBeforeCall(instructions, ReadListWithBits, Const.VanillaLobbyListLengthBits,
                    Const.LobbyListLengthBits, PatchId);
            }
        }

        private sealed class LobbyBeginRunSerializeListBitsPatch : IPatchMethod
        {
            public static string PatchId => "mlb_lobby_begin_run_serialize_list_bits";

            public static string Description => "Raise serialized lobby begin run player list width";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(LobbyBeginRunMessage), nameof(LobbyBeginRunMessage.Serialize)),
                ];
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return ReplaceBitWidthBeforeCall(instructions, WriteListWithBits, Const.VanillaLobbyListLengthBits,
                    Const.LobbyListLengthBits, PatchId);
            }
        }

        private sealed class LobbyBeginRunDeserializeListBitsPatch : IPatchMethod
        {
            public static string PatchId => "mlb_lobby_begin_run_deserialize_list_bits";

            public static string Description => "Raise deserialized lobby begin run player list width";

            public static ModPatchTarget[] GetTargets()
            {
                return
                [
                    new(typeof(LobbyBeginRunMessage), nameof(LobbyBeginRunMessage.Deserialize)),
                ];
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return ReplaceBitWidthBeforeCall(instructions, ReadListWithBits, Const.VanillaLobbyListLengthBits,
                    Const.LobbyListLengthBits, PatchId);
            }
        }
    }
}
