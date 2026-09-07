using FMOD;
using FMOD.Studio;
using HarmonyLib;
using SteamShelf;
using SteamShelf.Audio;
using System.Reflection;
using UnityEngine;

namespace Boxroom_MusicEX;

internal static class RadioDistanceController
{
    private static readonly FieldInfo ActiveRadioField = AccessTools.Field(typeof(Interactable_Radio), "ACTIVE_RADIO");
    private static readonly FieldInfo RadioEventField = AccessTools.Field(typeof(Interactable_Radio), "MUSIC_INSTANCE");
    private static readonly FieldInfo AlbumChannelField = AccessTools.Field(typeof(MusicPlayer), "channel");
    private static readonly FieldInfo AlbumPositionField = AccessTools.Field(typeof(MusicPlayer), "lastPosition");

    internal static void Update()
    {
        Camera listener = Camera.main;
        if (listener == null || Core.AudioDistance == null) return;

        float maximum = Mathf.Max(1f, Core.AudioDistance.Value);
        Interactable_Radio radio = ActiveRadioField?.GetValue(null) as Interactable_Radio;
        if (radio != null)
        {
            float level = Falloff(listener.transform.position, radio.transform.position, maximum);
            if (RadioEventField?.GetValue(null) is EventInstance radioEvent && radioEvent.isValid())
            {
                radioEvent.setVolume(level);
                if (radioEvent.getChannelGroup(out ChannelGroup group) == RESULT.OK && group.hasHandle())
                    group.set3DMinMaxDistance(0.5f, maximum);
            }
        }

        if (!Singleton<AudioManager>.HasInstance()) return;
        MusicPlayer music = Singleton<AudioManager>.Instance.Music;
        if (!music.IsPlayingAlbum) return;
        if (AlbumChannelField?.GetValue(music) is not Channel channel || !channel.hasHandle()) return;
        Vector3 position = AlbumPositionField?.GetValue(music) is Vector3 value ? value : radio?.transform.position ?? listener.transform.position;
        channel.set3DMinMaxDistance(0.5f, maximum);
        channel.setVolume(Falloff(listener.transform.position, position, maximum));
    }

    private static float Falloff(Vector3 listener, Vector3 source, float maximum) =>
        Mathf.Clamp01(Mathf.InverseLerp(maximum, 0.5f, Vector3.Distance(listener, source)));
}
