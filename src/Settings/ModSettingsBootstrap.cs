using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.Settings;

namespace STS2MultiplayerLimitBreak.Settings
{
    internal static class ModSettingsBootstrap
    {
        private static readonly Lock InitLock = new();
        private static bool _initialized;

        public static bool LimitBreakEnabled => ModData.Settings.LimitBreakEnabled;

        public static double ExtraPlayerScalingMultiplier => ClampExtraPlayerScalingMultiplier(
            ModData.Settings.ExtraPlayerScalingMultiplier);

        public static void Initialize()
        {
            lock (InitLock)
            {
                if (_initialized) return;

                IModSettingsValueBinding<bool> limitBreakBinding = ModSettingsBindings.WithDefault(
                    ModSettingsBindings.Global<ModSettings, bool>(
                        Const.ModId,
                        Const.SettingsKey,
                        settings => settings.LimitBreakEnabled,
                        (settings, value) =>
                        {
                            if (!CanEditSettings())
                                return;

                            settings.LimitBreakEnabled = value;
                            RuntimeMultiplayerSettings.PublishHostSettings("settings_changed");
                        }),
                    () => false);
                IModSettingsValueBinding<double> extraPlayerScalingMultiplierBinding = ModSettingsBindings.WithDefault(
                    ModSettingsBindings.Global<ModSettings, double>(
                        Const.ModId,
                        Const.SettingsKey,
                        settings => settings.ExtraPlayerScalingMultiplier,
                        (settings, value) =>
                        {
                            if (!CanEditSettings())
                                return;

                            settings.ExtraPlayerScalingMultiplier = ClampExtraPlayerScalingMultiplier(value);
                            RuntimeMultiplayerSettings.PublishHostSettings("settings_changed");
                        }),
                    () => ModSettings.DefaultExtraPlayerScalingMultiplier);

                RitsuLibFramework.RegisterModSettings(Const.ModId, page => page
                    .WithModDisplayName(ModSettingsLocalization.T("mod.displayName", "Multiplayer Limit Break"))
                    .WithTitle(ModSettingsLocalization.T("page.title", "Settings"))
                    .WithDescription(ModSettingsLocalization.T(
                        "page.description",
                        "Extends multiplayer lobby capacity only when enabled by the host."))
                    .WithReadOnlyOnHostSurfaces(ModSettingsHostSurface.RunPause | ModSettingsHostSurface.CombatPause)
                    .AddSection("compatibility", section => section
                        .WithTitle(ModSettingsLocalization.T("section.compatibility", "Compatibility"))
                        .AddToggle(
                            "limit_break_enabled",
                            ModSettingsLocalization.T("limitBreak.label", "Enable 16-player limit break"),
                            limitBreakBinding,
                            ModSettingsLocalization.T(
                                "limitBreak.description",
                                "When off, protocol bit widths and lobby limits stay vanilla-compatible.")))
                    .AddSection("scaling", section => section
                        .WithTitle(ModSettingsLocalization.T("section.scaling", "Player Scaling"))
                        .AddSlider(
                            "extra_player_scaling_multiplier",
                            ModSettingsLocalization.T(
                                "extraPlayerScalingMultiplier.label",
                                "Extra Player Scaling"),
                            extraPlayerScalingMultiplierBinding,
                            ModSettings.MinExtraPlayerScalingMultiplier,
                            ModSettings.MaxExtraPlayerScalingMultiplier,
                            0.05d,
                            value => $"{value:0.00}x",
                            ModSettingsLocalization.T(
                                "extraPlayerScalingMultiplier.description",
                                "Applies only when limit break is enabled. Values above 4 players scale as 4 plus extra players times this multiplier."))));

                _initialized = true;
            }
        }

        private static bool CanEditSettings()
        {
            return RunManager.Instance?.IsInProgress != true;
        }

        private static double ClampExtraPlayerScalingMultiplier(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return ModSettings.DefaultExtraPlayerScalingMultiplier;

            return Math.Clamp(
                value,
                ModSettings.MinExtraPlayerScalingMultiplier,
                ModSettings.MaxExtraPlayerScalingMultiplier);
        }
    }
}
