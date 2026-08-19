using MelonLoader;

[assembly: MelonInfo(typeof(Boxroom_MusicEX.Core), "Boxroom-MusicEX", "1.1.0", "MidgetBrony", null)]
[assembly: MelonGame("NestedLoop", "BOXROOM")]

namespace Boxroom_MusicEX
{
    /// <summary>
    /// MelonLoader entry point for MusicEX.
    ///
    /// The mod has two jobs:
    /// 1. Patch BOXROOM's one-level album scan so it visits nested folders.
    /// 2. Enrich each AlbumData object with standard audio tags and artwork fallbacks.
    /// </summary>
    public class Core : MelonMod
    {
        public override void OnInitializeMelon()
        {
            // Finds every [HarmonyPatch] class in this assembly and applies it.
            HarmonyInstance.PatchAll();

            // BOXROOM raises this event after constructing each album. Subscribing here lets
            // us improve that same AlbumData object before most game systems display it.
            SteamShelf.Media.Albums.AlbumLibrarySystem.OnAlbumReady += AlbumMetadataEnhancer.Enrich;
            LoggerInstance.Msg("Initialized with recursive album scanning.");
        }

        public override void OnDeinitializeMelon()
        {
            // Always detach static event handlers when unloading a mod. Otherwise the game can
            // retain the mod instance and call stale code after a reload.
            SteamShelf.Media.Albums.AlbumLibrarySystem.OnAlbumReady -= AlbumMetadataEnhancer.Enrich;
        }
    }
}
