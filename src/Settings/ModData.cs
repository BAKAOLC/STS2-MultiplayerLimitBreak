using STS2RitsuLib;
using STS2RitsuLib.Data;
using STS2RitsuLib.Utils.Persistence;

namespace STS2MultiplayerLimitBreak.Settings
{
    internal static class ModData
    {
        private static readonly ModDataStore Store =
            ModDataStore.For(Const.ModId);

        public static ModSettings Settings => Store.Get<ModSettings>(Const.SettingsKey);

        public static void Initialize()
        {
            using (RitsuLibFramework.BeginModDataRegistration(Const.ModId))
            {
                Store.Register(
                    Const.SettingsKey,
                    Const.SettingsFileName,
                    SaveScope.Global,
                    () => new ModSettings(),
                    true);
            }
        }
    }
}
