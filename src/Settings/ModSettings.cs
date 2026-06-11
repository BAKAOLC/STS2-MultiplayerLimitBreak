namespace STS2MultiplayerLimitBreak.Settings
{
    public sealed class ModSettings
    {
        public const bool DefaultLimitBreakEnabled = true;

        public const double MinExtraPlayerScalingMultiplier = 0.0d;

        public const double MaxExtraPlayerScalingMultiplier = 2.0d;

        public const double DefaultExtraPlayerScalingMultiplier = 1.0d;

        public int DataVersion { get; set; } = 1;

        public bool LimitBreakEnabled { get; set; } = DefaultLimitBreakEnabled;

        public double ExtraPlayerScalingMultiplier { get; set; } = DefaultExtraPlayerScalingMultiplier;
    }
}
