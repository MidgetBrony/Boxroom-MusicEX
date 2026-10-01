using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using SteamShelf.Media.Albums;
using TagLibSharp2.Core;
using UnityEngine;

namespace Boxroom_MusicEX
{
    /// <summary>
    /// Adds conventional music-library behavior to BOXROOM's AlbumData objects.
    ///
    /// Metadata priority is: embedded tags, recognizable folder layout, folder name,
    /// then the standard Unknown Artist / Unknown Album labels. Artwork priority is:
    /// BOXROOM's named cover file, embedded front cover, another suitable image file.
    /// Remaining images are passed to BOXROOM's existing inside-case renderers.
    /// </summary>
    internal static class AlbumMetadataEnhancer
    {
        private const string UnknownArtist = "Unknown Artist";
        private const string UnknownAlbum = "Unknown Album";
        private const int MaximumExtraImages = 16;

        // AlbumData exposes these values publicly but gives them internal setters. Because this
        // mod is a separate assembly, reflection is the least invasive way to update the game-
        // owned object without replacing AlbumLibrarySystem wholesale.
        private static readonly PropertyInfo DisplayNameProperty = AccessTools.Property(typeof(AlbumData), nameof(AlbumData.DisplayName));
        private static readonly PropertyInfo ArtistProperty = AccessTools.Property(typeof(AlbumData), nameof(AlbumData.Artist));
        private static readonly PropertyInfo TrackPathsProperty = AccessTools.Property(typeof(AlbumData), nameof(AlbumData.TrackPaths));
        private static readonly PropertyInfo CoverBytesProperty = AccessTools.Property(typeof(AlbumData), nameof(AlbumData.CoverArtBytes));
        private static readonly PropertyInfo CoverLoadedProperty = AccessTools.Property(typeof(AlbumData), nameof(AlbumData.CoverArtLoaded));
        private static readonly PropertyInfo ExtraArtProperty = AccessTools.Property(typeof(AlbumData), nameof(AlbumData.ExtraArtBytes));
        private static readonly PropertyInfo ProviderDataProperty = AccessTools.Property(typeof(AlbumDataProvider), nameof(AlbumDataProvider.Data));
        private static readonly MethodInfo NotifyMetadataReadyMethod = AccessTools.Method(typeof(AlbumDataProvider), "NotifyMetadataReady");

        // Unity's runtime image loader reliably accepts these formats in BOXROOM.
        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg" };

        // Start with the names used by BOXROOM, then include common media-player conventions.
        private static readonly string[] KnownCoverNames =
        {
            "cover.png", "cover.jpg", "cover.jpeg", "folder.png", "folder.jpg", "folder.jpeg",
            "album.png", "album.jpg", "album.jpeg", "front.png", "front.jpg", "front.jpeg",
            "albumart.png", "albumart.jpg", "albumart.jpeg", "artwork.png", "artwork.jpg", "artwork.jpeg"
        };

        internal static void Enrich(AlbumData album)
        {
            // Empty folders never become albums in BOXROOM, but this guard also makes the event
            // handler safe if another mod creates a partial AlbumData object.
            if (album == null || album.TrackPaths == null || album.TrackPaths.Count == 0) return;

            try
            {
                // Parse every track. BOXROOM originally reads only the artist of the first file,
                // which misses album title, track title, track number, and embedded pictures.
                List<TrackMetadata> tracks = album.TrackPaths.Select(ReadTrackMetadata).ToList();

                // More than one distinct Album tag means this folder is probably a hand-made
                // playlist rather than one release. In that case, preserve the folder identity,
                // original filename order, and folder artwork instead of letting the first song
                // redefine the entire BOXROOM case.
                bool isMixedAlbumPlaylist = tracks
                    .Select(track => Clean(track.Album))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .Count() > 1;

                // Tagged track numbers are the standard playback order. Untagged files follow in
                // filename order, preserving predictable behavior for plain WAV collections.
                if (!isMixedAlbumPlaylist)
                {
                    List<string> orderedPaths = tracks
                        .OrderBy(track => track.TrackNumber.HasValue && track.TrackNumber.Value > 0 ? 0 : 1)
                        .ThenBy(track => track.TrackNumber ?? uint.MaxValue)
                        .ThenBy(track => Path.GetFileName(track.Path), StringComparer.OrdinalIgnoreCase)
                        .Select(track => track.Path).ToList();
                    TrackPathsProperty.SetValue(album, orderedPaths);
                }

                // AlbumArtist is preferred over per-track Artist because compilations often have
                // a different performer on every song but one consistent release artist.
                string taggedAlbum = isMixedAlbumPlaylist ? string.Empty : FirstPopulated(tracks.Select(track => track.Album));
                string taggedArtist = isMixedAlbumPlaylist
                    ? CommonPopulated(tracks.Select(track => FirstPopulated(new[] { track.AlbumArtist, track.Artist })))
                    : FirstPopulated(tracks.Select(track => track.AlbumArtist));
                if (!isMixedAlbumPlaylist && string.IsNullOrWhiteSpace(taggedArtist))
                    taggedArtist = FirstPopulated(tracks.Select(track => track.Artist));

                InferFolderMetadata(album.FolderPath, taggedAlbum, taggedArtist, out string albumTitle, out string artist);
                DisplayNameProperty.SetValue(album, albumTitle);
                ArtistProperty.SetValue(album, artist);
                // This updates both front-cover bytes and the list BOXROOM paints inside a case.
                ApplyArtwork(album, tracks, allowEmbeddedCover: !isMixedAlbumPlaylist);
            }
            catch (Exception exception)
            {
                MelonLogger.Warning($"[MusicEX] Could not enrich album '{album.FolderPath}': {exception.Message}");
            }
        }

        internal static string BuildTrackList(IReadOnlyList<string> paths)
        {
            // AlbumBox normally prints raw filenames. Use embedded Title when present, while
            // retaining filename fallback for untagged WAV/MP3/FLAC/OGG files.
            if (paths == null || paths.Count == 0) return string.Empty;
            var lines = new List<string>(paths.Count);
            for (int index = 0; index < paths.Count; index++)
            {
                TrackMetadata metadata = ReadTrackMetadata(paths[index]);
                string title = string.IsNullOrWhiteSpace(metadata.Title)
                    ? Path.GetFileNameWithoutExtension(paths[index])
                    : metadata.Title.Trim();
                lines.Add($"{index + 1}. {title}");
            }
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        private static TrackMetadata ReadTrackMetadata(string path)
        {
            // TagLibSharp2 is already shipped with BOXROOM and understands all audio formats the
            // base album scanner accepts, so no extra native dependency is introduced.
            var metadata = new TrackMetadata { Path = path };
            try
            {
                MediaFileResult result = MediaFile.Read(path);
                if (!result.IsSuccess || result.Tag == null) return metadata;

                Tag tag = result.Tag;
                metadata.Title = Clean(tag.Title);
                metadata.Album = Clean(tag.Album);
                metadata.AlbumArtist = Clean(tag.AlbumArtist);
                metadata.Artist = Clean(tag.Artist);
                if (string.IsNullOrWhiteSpace(metadata.Artist) && tag.Performers != null)
                    metadata.Artist = FirstPopulated(tag.Performers);
                metadata.TrackNumber = tag.Track;

                if (tag.Pictures != null)
                {
                    // Keep all distinct embedded pictures. The front-cover type is selected for
                    // the case exterior later; back/booklet images can be used on the interior.
                    foreach (IPicture picture in tag.Pictures)
                    {
                        byte[] bytes = picture == null ? null : picture.PictureData.ToArray();
                        if (bytes != null && bytes.Length > 0)
                        {
                            metadata.Pictures.Add(new EmbeddedPicture
                            {
                                Bytes = bytes,
                                IsFrontCover = picture.PictureType.ToString().Equals("FrontCover", StringComparison.OrdinalIgnoreCase),
                                IsGenericCover = picture.PictureType.ToString().Equals("Other", StringComparison.OrdinalIgnoreCase)
                            });
                        }
                    }
                }
            }
            catch
            {
                // Untagged or malformed media receives folder and filename fallbacks.
            }
            return metadata;
        }

        private static void InferFolderMetadata(string folderPath, string taggedAlbum, string taggedArtist, out string album, out string artist)
        {
            // Supported folder conventions:
            //   Music\Artist\Album\tracks
            //   Music\Artist - Album\tracks
            // A folder directly below the library root is an album, not automatically an artist.
            string folderName = Clean(Path.GetFileName(folderPath));
            SplitArtistAndAlbum(folderName, out string parsedArtist, out string parsedAlbum);
            album = FirstPopulated(new[] { taggedAlbum, parsedAlbum, folderName, UnknownAlbum });
            artist = FirstPopulated(new[] { taggedArtist, parsedArtist });

            if (string.IsNullOrWhiteSpace(artist))
            {
                // Only use the parent as artist when it is below the selected library root.
                // This prevents the root folder itself (for example "My Music") becoming artist.
                string root = AlbumLibrarySystem.NormalizePath(AlbumLibrarySystem.GetSourceRoot());
                DirectoryInfo parent = Directory.GetParent(folderPath);
                if (parent != null && !AlbumLibrarySystem.NormalizePath(parent.FullName).Equals(root, StringComparison.OrdinalIgnoreCase))
                    artist = Clean(parent.Name);
            }
            if (string.IsNullOrWhiteSpace(album)) album = UnknownAlbum;
            if (string.IsNullOrWhiteSpace(artist)) artist = UnknownArtist;
        }

        private static void SplitArtistAndAlbum(string folderName, out string artist, out string album)
        {
            // Accept hyphen, en dash, and em dash separators commonly produced by music tools.
            artist = string.Empty;
            album = string.Empty;
            if (string.IsNullOrWhiteSpace(folderName)) return;
            foreach (string separator in new[] { " - ", " – ", " — " })
            {
                int separatorIndex = folderName.IndexOf(separator, StringComparison.Ordinal);
                if (separatorIndex > 0 && separatorIndex + separator.Length < folderName.Length)
                {
                    artist = folderName.Substring(0, separatorIndex).Trim();
                    album = folderName.Substring(separatorIndex + separator.Length).Trim();
                    return;
                }
            }
        }

        private static void ApplyArtwork(AlbumData album, IReadOnlyList<TrackMetadata> tracks, bool allowEmbeddedCover)
        {
            // Deduplicate embedded art repeated in every track of an album. Comparing bytes costs
            // little here and avoids loading the same large cover into memory many times.
            var embedded = new List<EmbeddedPicture>();
            if (allowEmbeddedCover)
            {
                foreach (TrackMetadata track in tracks)
                    foreach (EmbeddedPicture picture in track.Pictures)
                        if (!embedded.Any(existing => ByteArraysEqual(existing.Bytes, picture.Bytes))) embedded.Add(picture);
            }

            // File discovery is deliberately limited to the album folder. Artwork in an artist
            // parent should not leak into every child album.
            string[] imageFiles = GetImageFiles(album.FolderPath);
            string selectedCoverFile = FindLikelyCoverFile(imageFiles);
            bool needsCover = !album.CoverArtLoaded || album.CoverArtBytes == null || album.CoverArtBytes.Length == 0;
            EmbeddedPicture selectedEmbeddedCover = allowEmbeddedCover
                ? embedded.FirstOrDefault(picture => picture.IsFrontCover)
                    ?? embedded.FirstOrDefault(picture => picture.IsGenericCover)
                : null;
            if (selectedEmbeddedCover == null && allowEmbeddedCover && needsCover)
                selectedEmbeddedCover = embedded.FirstOrDefault();

            if (needsCover)
            {
                // Never overwrite a cover BOXROOM already loaded. If it has none, prefer the
                // embedded FrontCover picture, then any embedded picture, then a suitable file.
                if (selectedEmbeddedCover != null)
                {
                    CoverBytesProperty.SetValue(album, selectedEmbeddedCover.Bytes);
                    CoverLoadedProperty.SetValue(album, true);
                }
                else if (selectedCoverFile != null)
                {
                    byte[] fileBytes = TryReadImage(selectedCoverFile);
                    if (fileBytes != null)
                    {
                        CoverBytesProperty.SetValue(album, fileBytes);
                        CoverLoadedProperty.SetValue(album, true);
                    }
                }
            }

            // BOXROOM's AlbumBox maps ExtraArtBytes to renderers inside the open jewel case.
            // Any non-cover image can participate; users are no longer restricted to a gapless
            // extra_0.jpg, extra_1.jpg naming sequence.
            var extras = new List<byte[]>();
            foreach (string imageFile in imageFiles)
            {
                if (IsSamePath(imageFile, selectedCoverFile) || IsKnownCoverName(Path.GetFileName(imageFile))) continue;
                AddUnique(extras, TryReadImage(imageFile));
                if (extras.Count >= MaximumExtraImages) break;
            }
            foreach (EmbeddedPicture picture in embedded)
            {
                // Do not repeat the exterior/front cover on the inside unless a separate disk
                // image file explicitly contains the same art.
                if (ReferenceEquals(picture, selectedEmbeddedCover) || picture.IsFrontCover) continue;
                AddUnique(extras, picture.Bytes);
                if (extras.Count >= MaximumExtraImages) break;
            }
            ExtraArtProperty.SetValue(album, extras);
            RefreshLiveAlbumProviders(album);
        }

        /// <summary>
        /// BOXROOM chooses the next extra_N filename from ExtraArtBytes.Count. Metadata images
        /// also live in that list, so temporarily reduce it to the contiguous on-disk sequence
        /// before BOXROOM writes a user screenshot. The normal OnAlbumReady enrichment rebuilds
        /// the complete folder-plus-metadata list immediately afterwards.
        /// </summary>
        internal static void PrepareForUserExtraArt(AlbumData album)
        {
            if (album == null || string.IsNullOrWhiteSpace(album.FolderPath)) return;

            var persisted = new List<byte[]>();
            for (int index = 0; index < MaximumExtraImages; index++)
            {
                string path = FindPersistedExtraPath(album.FolderPath, index);
                if (path == null) break;
                byte[] bytes = TryReadImage(path);
                if (bytes == null) break;
                persisted.Add(bytes);
            }
            ExtraArtProperty.SetValue(album, persisted);
        }

        private static string FindPersistedExtraPath(string folderPath, int index)
        {
            string jpg = Path.Combine(folderPath, $"extra_{index}.jpg");
            if (File.Exists(jpg)) return jpg;
            string png = Path.Combine(folderPath, $"extra_{index}.png");
            return File.Exists(png) ? png : null;
        }

        private static void RefreshLiveAlbumProviders(AlbumData album)
        {
            if (album == null || ProviderDataProperty == null || NotifyMetadataReadyMethod == null) return;
            try
            {
                foreach (AlbumDataProvider provider in Resources.FindObjectsOfTypeAll<AlbumDataProvider>())
                {
                    if (provider == null || provider.AlbumId != album.Id) continue;
                    ProviderDataProperty.SetValue(provider, album);
                    NotifyMetadataReadyMethod.Invoke(provider, null);
                }
            }
            catch (Exception exception)
            {
                MelonLogger.Warning($"[MusicEX] Could not refresh album artwork for '{album.FolderPath}': {exception.Message}");
            }
        }

        private static string[] GetImageFiles(string folderPath)
        {
            // A bad or temporarily unavailable artwork file should not discard the entire album.
            try
            {
                return Directory.GetFiles(folderPath)
                    .Where(path => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        private static string FindLikelyCoverFile(IEnumerable<string> imageFiles)
        {
            // Known conventional names win. Otherwise use the first general image, but never
            // promote obviously interior-only art such as back.jpg or extra_0.png to the cover.
            foreach (string knownName in KnownCoverNames)
            {
                string match = imageFiles.FirstOrDefault(path => Path.GetFileName(path).Equals(knownName, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return imageFiles.FirstOrDefault(path => !IsInteriorArtName(Path.GetFileNameWithoutExtension(path)));
        }

        private static bool IsInteriorArtName(string name)
        {
            // Prefix matching also accepts useful variants such as booklet-2.jpg and disc1.png.
            if (string.IsNullOrWhiteSpace(name)) return false;
            return name.StartsWith("extra_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("back", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("inside", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("inlay", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("booklet", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("disc", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("cd", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsKnownCoverName(string fileName) => KnownCoverNames.Any(name => name.Equals(fileName, StringComparison.OrdinalIgnoreCase));

        private static byte[] TryReadImage(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { byte[] bytes = File.ReadAllBytes(path); return bytes.Length > 0 ? bytes : null; }
            catch { return null; }
        }

        private static void AddUnique(List<byte[]> destination, byte[] bytes)
        {
            // The same image may exist both as a file and as an embedded tag picture.
            if (bytes != null && bytes.Length > 0 && !destination.Any(existing => ByteArraysEqual(existing, bytes))) destination.Add(bytes);
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int index = 0; index < left.Length; index++) if (left[index] != right[index]) return false;
            return true;
        }

        private static bool IsSamePath(string left, string right)
        {
            return left != null && right != null && Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string FirstPopulated(IEnumerable<string> values) =>
            values?.Select(Clean).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

        private static string CommonPopulated(IEnumerable<string> values)
        {
            string[] distinct = values.Select(Clean)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();
            return distinct.Length == 1 ? distinct[0] : string.Empty;
        }

        private static string Clean(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

        private sealed class TrackMetadata
        {
            // Small internal transfer object: keeps tag parsing separate from AlbumData mutation.
            internal string Path;
            internal string Title;
            internal string Album;
            internal string AlbumArtist;
            internal string Artist;
            internal uint? TrackNumber;
            internal readonly List<EmbeddedPicture> Pictures = new List<EmbeddedPicture>();
        }

        private sealed class EmbeddedPicture
        {
            internal byte[] Bytes;
            internal bool IsFrontCover;
            internal bool IsGenericCover;
        }
    }

    [HarmonyPatch(typeof(AlbumLibrarySystem), nameof(AlbumLibrarySystem.ApplyUserExtraArt))]
    internal static class AlbumUserExtraArtPatch
    {
        private static void Prefix(AlbumData album)
        {
            if (Core.EnableMetadataEnhancement?.Value == true)
                AlbumMetadataEnhancer.PrepareForUserExtraArt(album);
        }
    }

    [HarmonyPatch(typeof(AlbumBox), "BuildTrackList")]
    internal static class AlbumTrackTitlesPatch
    {
        /// <summary>
        /// Replaces AlbumBox's filename-only list with the tag-aware list above. Returning false
        /// tells Harmony that __result is complete and the original private method should not run.
        /// </summary>
        private static bool Prefix(List<string> paths, ref string __result)
        {
            // Returning true preserves BOXROOM's original filename-based list when the optional
            // metadata feature is disabled.
            if (Core.EnableMetadataEnhancement?.Value != true)
            {
                return true;
            }

            __result = AlbumMetadataEnhancer.BuildTrackList(paths);
            return false;
        }
    }
}
