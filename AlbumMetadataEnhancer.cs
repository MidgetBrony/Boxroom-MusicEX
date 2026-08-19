using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using SteamShelf.Media.Albums;
using TagLibSharp2.Core;

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

                // Tagged track numbers are the standard playback order. Untagged files follow in
                // filename order, preserving predictable behavior for plain WAV collections.
                List<string> orderedPaths = tracks
                    .OrderBy(track => track.TrackNumber.HasValue && track.TrackNumber.Value > 0 ? 0 : 1)
                    .ThenBy(track => track.TrackNumber ?? uint.MaxValue)
                    .ThenBy(track => Path.GetFileName(track.Path), StringComparer.OrdinalIgnoreCase)
                    .Select(track => track.Path).ToList();
                TrackPathsProperty.SetValue(album, orderedPaths);

                // AlbumArtist is preferred over per-track Artist because compilations often have
                // a different performer on every song but one consistent release artist.
                string taggedAlbum = FirstPopulated(tracks.Select(track => track.Album));
                string taggedArtist = FirstPopulated(tracks.Select(track => track.AlbumArtist));
                if (string.IsNullOrWhiteSpace(taggedArtist)) taggedArtist = FirstPopulated(tracks.Select(track => track.Artist));

                InferFolderMetadata(album.FolderPath, taggedAlbum, taggedArtist, out string albumTitle, out string artist);
                DisplayNameProperty.SetValue(album, albumTitle);
                ArtistProperty.SetValue(album, artist);
                // This updates both front-cover bytes and the list BOXROOM paints inside a case.
                ApplyArtwork(album, tracks);
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
                                IsFrontCover = picture.PictureType.ToString().Equals("FrontCover", StringComparison.OrdinalIgnoreCase)
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

        private static void ApplyArtwork(AlbumData album, IReadOnlyList<TrackMetadata> tracks)
        {
            // Deduplicate embedded art repeated in every track of an album. Comparing bytes costs
            // little here and avoids loading the same large cover into memory many times.
            var embedded = new List<EmbeddedPicture>();
            foreach (TrackMetadata track in tracks)
                foreach (EmbeddedPicture picture in track.Pictures)
                    if (!embedded.Any(existing => ByteArraysEqual(existing.Bytes, picture.Bytes))) embedded.Add(picture);

            // File discovery is deliberately limited to the album folder. Artwork in an artist
            // parent should not leak into every child album.
            string[] imageFiles = GetImageFiles(album.FolderPath);
            string selectedCoverFile = FindLikelyCoverFile(imageFiles);
            byte[] selectedEmbeddedCover = null;

            if (!album.CoverArtLoaded || album.CoverArtBytes == null || album.CoverArtBytes.Length == 0)
            {
                // Never overwrite a cover BOXROOM already loaded. If it has none, prefer the
                // embedded FrontCover picture, then any embedded picture, then a suitable file.
                EmbeddedPicture front = embedded.FirstOrDefault(picture => picture.IsFrontCover) ?? embedded.FirstOrDefault();
                if (front != null)
                {
                    selectedEmbeddedCover = front.Bytes;
                    CoverBytesProperty.SetValue(album, front.Bytes);
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
                if (ByteArraysEqual(picture.Bytes, selectedEmbeddedCover) || picture.IsFrontCover) continue;
                AddUnique(extras, picture.Bytes);
                if (extras.Count >= MaximumExtraImages) break;
            }
            ExtraArtProperty.SetValue(album, extras);
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
            __result = AlbumMetadataEnhancer.BuildTrackList(paths);
            return false;
        }
    }
}
