using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using STS2MultiplayerLimitBreak.Layout;
using STS2MultiplayerLimitBreak.Network;
using STS2MultiplayerLimitBreak.Settings;
using STS2RitsuLib;

namespace STS2MultiplayerLimitBreak
{
    [ModInitializer(nameof(Initialize))]
    public static class ModEntry
    {
        private static bool IsActive { get; set; } = true;

        public static void Initialize()
        {
            ModData.Initialize();
            ModSettingsBootstrap.Initialize();
            RuntimeMultiplayerSettings.Initialize();

            ApplyNetworkPatches();
            if (!IsActive) return;

            ApplyLayoutPatches();
            if (!IsActive) return;

            ApplyDifficultyScalingPatches();
            if (!IsActive) return;

            Log.Info($"{Const.ModId} loaded. Limit break enabled: {RuntimeMultiplayerSettings.LimitBreakEnabled}.");
        }

        private static void ApplyNetworkPatches()
        {
            var patcher = RitsuLibFramework.CreatePatcher(Const.ModId, "network", "multiplayer limit");
            MultiplayerLimitPatches.AddTo(patcher);
            SerializationBitWidthPatches.AddTo(patcher);
            RitsuLibFramework.ApplyRequiredPatcher(
                patcher,
                DisableMod,
                "Required multiplayer limit patches failed. STS2-MultiplayerLimitBreak will be disabled.");
        }

        private static void ApplyLayoutPatches()
        {
            var patcher = RitsuLibFramework.CreatePatcher(Const.ModId, "layout", "multiplayer layout");
            MerchantRoomLayoutPatches.AddTo(patcher);
            RestSiteRoomLayoutPatches.AddTo(patcher);
            TreasureRoomRelicLayoutPatches.AddTo(patcher);
            RitsuLibFramework.ApplyRequiredPatcher(
                patcher,
                DisableMod,
                "Required multiplayer layout patches failed. STS2-MultiplayerLimitBreak will be disabled.");
        }

        private static void ApplyDifficultyScalingPatches()
        {
            var patcher = RitsuLibFramework.CreatePatcher(Const.ModId, "difficulty_scaling", "multiplayer scaling");
            DifficultyScalingPatches.AddTo(patcher);
            RitsuLibFramework.ApplyRequiredPatcher(
                patcher,
                DisableMod,
                "Required multiplayer scaling patches failed. STS2-MultiplayerLimitBreak will be disabled.");
        }

        private static void DisableMod()
        {
            IsActive = false;
        }
    }
}
