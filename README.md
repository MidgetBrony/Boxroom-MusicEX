# BOXROOM MusicEX

MusicEX improves the Albums music library in BOXROOM. It scans nested music folders, reads standard audio metadata, finds more kinds of album artwork, and uses additional images inside the open album case.

Version 1.3.1 keeps user-added screenshots in BOXROOM's contiguous `extra_0`, `extra_1`, ... sequence when metadata mode is enabled, prevents a generically tagged embedded cover from appearing as interior artwork, and refreshes a live album case after artwork changes.

## Features

- Recursively finds albums below the selected music-library folder.
- Supports BOXROOM's WAV, MP3, FLAC, and OGG album formats.
- Reads album title, album artist, track title, and track number from embedded tags.
- Sorts playback by embedded track number, then by filename.
- Reads conventional folder artwork such as `cover.jpg`, `folder.png`, `front.jpg`, and `albumart.jpg`.
- Uses embedded front-cover artwork when no cover file exists.
- Uses additional folder or embedded images on the inside of the open album case.
- Falls back to common folder layouts when tags are missing.
- Provides a live distance control for BOXROOM radio and album audio.

## Installation

1. Install [MelonLoader](https://melonwiki.xyz/) for BOXROOM.
2. Install `ModsPanel.dll` in BOXROOM's `Mods` folder.
3. Close BOXROOM if it is running.
4. Copy `Boxroom_MusicEX.dll` into the game's `Mods` folder. A standard Steam installation will resemble:

   ```text
   ...\steamapps\common\My Game Room\Mods\Boxroom_MusicEX.dll
   ```

5. Start BOXROOM.
6. Open the Albums music-library setting and select the folder containing your music.
7. Rescan the Albums library if BOXROOM has already scanned that folder before.

The MelonLoader console should report that MusicEX initialized and queued folders for BOXROOM's album scan.

## Organizing Music

The recommended layout is one folder per album:

```text
Music
└── Artist Name
    └── Album Name
        ├── 01 - First Track.flac
        ├── 02 - Second Track.flac
        ├── cover.jpg
        ├── back.jpg
        └── booklet.png
```

MusicEX also understands a combined folder name:

```text
Music\Artist Name - Album Name\tracks...
```

Metadata is selected in this order:

1. Embedded audio tags.
2. `Artist - Album` folder naming.
3. `Artist\Album` folder layout.
4. The album folder's name.
5. `Unknown Artist` or `Unknown Album` when nothing else is available.

For untagged tracks, the filename is shown as the track title.

## Optional Metadata Enhancement

Recursive folder scanning is always enabled. Embedded metadata enhancement is optional and defaults to **off** so playlist-style folders keep BOXROOM's original folder names and filename order.

To enable embedded album, artist, track-number, track-title, and artwork metadata:

1. Open BOXROOM's **Mods** tab.
2. Select **Mod Settings**.
3. Open the **BOXROOM MusicEX** section.
4. Enable **Use embedded music metadata**. MusicEX immediately rescans the Albums library.

When **ON**, MusicEX uses embedded album, artist, track-title, track-number, and artwork tags. This is best when each folder represents one real album.

When **OFF**, MusicEX keeps BOXROOM's folder-based album names, filename order, and folder artwork. This is recommended for playlist or mix folders. Disabling the checkbox rescans immediately to restore that behavior.

Advanced users can set the same preference manually in `UserData\MelonPreferences.cfg`:

```ini
[Boxroom-MusicEX]
EnableMetadataEnhancement = true
```

Leave this option disabled if a folder is a manually assembled playlist containing songs from unrelated albums. When enhancement is enabled, MusicEX also detects multiple distinct Album tags in one folder and preserves that folder's name, filename order, and folder artwork instead of adopting the first track's album information.

## Radio and Album Audio Distance

Open **Mods → Mod Settings → BOXROOM MusicEX** and adjust **Radio / album audio distance** to control how far radio and album playback carries through the room. The range is 1–25 metres and defaults to 8 metres. Changes apply while audio is playing and are saved in MelonPreferences.

## Album Artwork

For the front cover, MusicEX prefers:

1. A conventional cover file in the album folder.
2. Embedded front-cover artwork from an audio file.
3. Another suitable PNG or JPEG in the album folder.

Common cover filenames include `cover`, `folder`, `album`, `front`, `albumart`, and `artwork`, using `.png`, `.jpg`, or `.jpeg`.

Additional images are used inside the album case. Names such as `extra_0.jpg`, `back.jpg`, `inside.png`, `inlay.jpg`, `booklet.png`, and `disc.jpg` work, but additional PNG and JPEG files do not require a special name. MusicEX loads up to 16 distinct extra images.

## Troubleshooting

- Close BOXROOM before replacing the DLL; Windows locks loaded mod files.
- Rescan the Albums library after changing tags, folder names, or artwork.
- Keep each album's tracks directly inside its album folder.
- MusicEX skips unreadable folders and avoids recursively following directory junctions or symbolic links.
- If the console says the album scan could not be patched, a BOXROOM update may have changed the underlying method.

## Building from Source

### Requirements

- Windows x64
- The .NET SDK
- BOXROOM with MelonLoader installed
- A local copy of this repository

The project references the assemblies shipped with BOXROOM and MelonLoader; no game files are included in this repository.

### Configure the game path

Open `Directory.Build.props` and set `GamePath` to your BOXROOM installation directory:

```xml
<Project>
  <PropertyGroup>
    <GamePath>D:\SteamLibrary\steamapps\common\My Game Room</GamePath>
  </PropertyGroup>
</Project>
```

Do not include a trailing `BOXROOM_Data` or `Mods` segment.

### Build

Close BOXROOM, open a terminal in the repository, and run:

```powershell
dotnet build .\Boxroom-MusicEX.csproj -c Release
```

The compiled mod is created at:

```text
bin\Release\netstandard2.1\Boxroom_MusicEX.dll
```

The project's post-build step also copies the DLL into `<GamePath>\Mods`. If BOXROOM is running, that copy will fail because the loaded DLL is locked.

## License

Released under the MIT License. See [LICENSE.txt](LICENSE.txt).
