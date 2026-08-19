# BOXROOM MusicEX

MusicEX improves the Albums music library in BOXROOM. It scans nested music folders, reads standard audio metadata, finds more kinds of album artwork, and uses additional images inside the open album case.

## Features

- Recursively finds albums below the selected music-library folder.
- Supports BOXROOM's WAV, MP3, FLAC, and OGG album formats.
- Reads album title, album artist, track title, and track number from embedded tags.
- Sorts playback by embedded track number, then by filename.
- Reads conventional folder artwork such as `cover.jpg`, `folder.png`, `front.jpg`, and `albumart.jpg`.
- Uses embedded front-cover artwork when no cover file exists.
- Uses additional folder or embedded images on the inside of the open album case.
- Falls back to common folder layouts when tags are missing.

## Installation

1. Install [MelonLoader](https://melonwiki.xyz/) for BOXROOM.
2. Close BOXROOM if it is running.
3. Copy `Boxroom_MusicEX.dll` into the game's `Mods` folder. A standard Steam installation will resemble:

   ```text
   ...\steamapps\common\My Game Room\Mods\Boxroom_MusicEX.dll
   ```

4. Start BOXROOM.
5. Open the Albums music-library setting and select the folder containing your music.
6. Rescan the Albums library if BOXROOM has already scanned that folder before.

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
