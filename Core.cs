using MelonLoader;

[assembly: MelonInfo(typeof(Boxroom_MusicEX.Core), "Boxroom-MusicEX", "1.2.1", "MidgetBrony", null)]
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
        /// <summary>
        /// Controls only tag-based enrichment. Recursive folder discovery remains active even
        /// when this is false, which lets playlist-style libraries use the original BOXROOM
        /// folder names and ordering.
        /// </summary>
        internal static MelonPreferences_Entry<bool> EnableMetadataEnhancement;

        private static bool metadataHandlerSubscribed;

        public override void OnInitializeMelon()
        {
            MelonPreferences_Category preferences = MelonPreferences.CreateCategory("Boxroom-MusicEX");
            EnableMetadataEnhancement = preferences.CreateEntry(
                "EnableMetadataEnhancement",
                false,
                "Enable album metadata enhancement",
                "Reads embedded album/artist/title/track-number/artwork tags. Disable for playlist folders containing songs from different albums.");

            RegisterModsPanelSettings();

            // Finds every [HarmonyPatch] class in this assembly and applies it.
            HarmonyInstance.PatchAll();

            SetMetadataHandlerSubscription(EnableMetadataEnhancement.Value);

            LoggerInstance.Msg($"Initialized. Recursive scanning: enabled; metadata enhancement: {(EnableMetadataEnhancement.Value ? "enabled" : "disabled")}.");
        }

        public override void OnDeinitializeMelon()
        {
            // Always detach static event handlers when unloading a mod. Otherwise the game can
            // retain the mod instance and call stale code after a reload.
            SetMetadataHandlerSubscription(false);
        }

        /// <summary>
        /// Updates the preference and the live event subscription, then rebuilds the album
        /// registry. Rescanning is important when switching off: it replaces already-enriched
        /// AlbumData objects with BOXROOM's original folder-based versions.
        /// </summary>
        internal static void SetMetadataEnhancement(bool enabled, bool rescanLibrary)
        {
            if (EnableMetadataEnhancement == null) return;

            EnableMetadataEnhancement.Value = enabled;
            MelonPreferences.Save();
            SetMetadataHandlerSubscription(enabled);

            if (rescanLibrary)
            {
                _ = SteamShelf.Media.Albums.AlbumLibrarySystem.ScanLibraryAsync();
            }
        }

        private static void SetMetadataHandlerSubscription(bool subscribe)
        {
            if (subscribe && !metadataHandlerSubscribed)
            {
                // BOXROOM raises this event after constructing each album. Subscribing here lets
                // us improve that same AlbumData object before most game systems display it.
                SteamShelf.Media.Albums.AlbumLibrarySystem.OnAlbumReady += AlbumMetadataEnhancer.Enrich;
                metadataHandlerSubscribed = true;
            }
            else if (!subscribe && metadataHandlerSubscribed)
            {
                SteamShelf.Media.Albums.AlbumLibrarySystem.OnAlbumReady -= AlbumMetadataEnhancer.Enrich;
                metadataHandlerSubscribed = false;
            }
        }

        private static void RegisterModsPanelSettings()
        {
            global::ModsPanel.ModsPanelApi
                .RegisterSection("MidgetBrony.Boxroom-MusicEX", "BOXROOM MusicEX", 110)
                .Clear()
                .AddToggle(
                    "enable-metadata",
                    "Use embedded music metadata",
                    () => EnableMetadataEnhancement?.Value == true,
                    enabled => SetMetadataEnhancement(enabled, rescanLibrary: true))
                .AddLabel(
                    "metadata-warning",
                    "ON: Uses embedded album, artist, track title, track number, and artwork tags. Best when each folder is one real album.\n\nOFF: Keeps BOXROOM's folder-based album names, filename order, and folder artwork. Recommended for playlist or mix folders.");
        }
    }
}
