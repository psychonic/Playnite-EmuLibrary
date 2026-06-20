# EmuLibrary

EmuLibrary is a library extension for [Playnite](https://www.playnite.link), an open source video game library manager, focused on emulator ROM management.

While Playnite has had built-in support for scanning paths for installed ROMs and adding them to the library since version 9, EmuLibrary provides alternate functionality.

EmuLibrary treats one or more folders of ROMs/Disc images as a library from which you can "install" games. It can be useful if you have a large collection of emulated games and limited storage where you play them versus where you store them (HTPC vs. NAS, for example). It also is useful for keeping the list of emulated games up to date, and for being able to filter via installed/uninstalled.

Disclaimer: I created this extension for my own usage, and that is still the primary focus. Because of this, many parts of it are still tailored to my personal needs and usage patterns. Despite that, I wanted to share it with others in case it is useful to them. It is still in the process of being (slowly) adapted for more general use.

## Setup

To set it up, you create mappings to combine one of each of the following:

* Emulator - either a built-in emulator or a custom emulator manually added
* Emulator Profile - either a built-in emulator profile or a custom one, out of those supported by the chosen emulator
* Platform - the ROM platform/console, out of those that the emulator profile supports
* RomType - See [Rom Types](#rom-types) below

## Paths

For source and destination, only valid Windows file paths are currently supported. The intended use case is for having the source be an SMB file share (either via UNC path or mapped drive), and the destination be a local path. However, any valid file path should work for either. This means that you can get creative with the source if you have a way to mount alternate remote storage at a Windows file path.

Additionally, for destination paths, relativity to the Playnite folder is preserved if you are using a portable installation of Playnite and your destination is below that folder hierarchically. This means that, for example, if your portable installation is at D:\playnite, and you choose `D:\playnite\rominstall` as your destination, it will be saved internally as `{PlayniteDir}\rominstall`.

## Rom Types

### SingleFile

SingleFile is the simplest type of ROM supported. This is for source folders in which each ROM is fully contained in a single file. It's commonly used for older, non-disc-based systems where the whole ROM consists of a single file. (Ex. .nes, .sfc, .md, etc.). Archive formats are supported as well if the emulator supports them directly. (Ex. .zip)

### MultiFile

With the MultiFile type, each subfolder directly within the source folder is scanned as a potential "ROM". This is for games that have multiple loose files. (Ex. one or more .bin/.cue, with optional .m3u). When installing a MultiFile game, the whole folder is copied. 

To determine which file is used as the one to tell the emulator to load, all files matching the configured emulator profile's supported extensions are considered. Precedence is by configured image extension list order, and then by alphabetical order. For example, if file names are the same except for `(Disc 1)` versus `(Disc 2)`, the first disc takes precedence. Similarly, if you have `.cue` in the extension list before `.m3u` (as some of the built-in profiles have at the time of writing), `.cue` would be chosen over `.m3u`, which may not be desired for multi-disc games.

### Yuzu

The Yuzu type supports Nintendo Switch emulators that share Yuzu's architecture: **Yuzu**, **Eden**, and **Citron-Neo**. Select which emulator to use via the dropdown in the mapping settings — this controls where the plugin looks for keys and the NAND directory.

To add a functional mapping, select the appropriate emulator in the dropdown, then set the Playnite emulator entry to match (it does not need to be a built-in listing; custom entries work fine). In the source path, loose XCI/NSP/XCZ/NSZ files in the root of the path are considered.

Unlike the SingleFile/MultiFile types, this type scans by its own fixed list of formats (XCI/NSP/XCZ/NSZ) rather than the emulator profile's configured image extensions, so the profile does not need any image extensions defined.

NSP/NSZ files can also be updates and DLC, rather than just games. Unlike with Tinfoil shares, files are not required to include the title id in the filename. Additionally, while destination path must point to a folder that exists, the setting is ignored. Games install into the NAND directory configured in the selected emulator profile.

When a game is installed, the latest update and any DLC from the source will also be installed to the emulator's NAND, in that order (Game, Update if available, each available DLC). Games already installed will be imported, whether or not they exist in the source folder, and will display as installed. As expected, uninstalling a game will remove the game from the NAND. (While these emulators do not support XCZ or NSZ files for launching or installing to NAND, this plugin installs directly to the NAND, without relying on the emulator's built-in install functionality)

Game names and other details are enriched from an online database where possible — see [Metadata Enrichment](#metadata-enrichment).

### PS3 (Beta)

The PS3 type adds support for PlayStation 3 games run under [RPCS3](https://rpcs3.net). It has a beta level of quality of support. Select RPCS3 as the emulator for the mapping (built-in or custom).

Like the Yuzu type, this type scans by its own format logic (`.iso`/`.pkg`/`.rap`) rather than the emulator profile's configured image extensions, so the profile does not need any image extensions defined — RPCS3's built-in profile declares none, and that is fine.

Unlike the other types, a PS3 "game" is a **composite**: a base (either a disc image or a downloadable PKG game) plus any number of update PKGs, DLC PKGs, and RAP license files. The scanner treats **each immediate subfolder of the source path as one title**, and the recommended layout groups a title's content together:

```
<SourcePath>/
  BLES01234 - Demon's Souls/
    BLES01234 - Demon's Souls.iso        # encrypted disc image
    BLES01234 - Demon's Souls.dkey       # disc key (matching basename — required by RPCS3)
    updates/  <patch>.pkg ...            # installed in APP_VER order (from each PKG, never the filename)
    dlc/      <dlc>.pkg ...
    licenses/ EP0001-BLES01234_00-DLC0001.rap   # filename is the content-id
  NPUB30001 - Some PSN Game/
    NPUB30001 - Some PSN Game.pkg        # PKG base game (no disc image)
    updates/ ...
    dlc/ ...
    licenses/ ...
```

The `updates/`, `dlc/`, and `licenses/` subfolders are optional and case-insensitive. The scanner is tolerant of looser layouts too — PKGs are classified by their own metadata (the PKG patch flag plus PARAM.SFO fields), so a PKG dropped anywhere under the title folder is still categorized correctly; the subfolder name is used as an additional hint.

Installing a title can touch two destinations:

* **Disc bases** (an encrypted `.iso` plus its matching `.dkey`, or a decrypted `.iso`/`PS3_GAME` folder) are copied to the mapping's **destination path**. The `.dkey` sidecar is copied alongside the image so RPCS3 can load the encrypted ISO directly — there is no decrypt-on-install step.
* **PKG content** (a PKG base game, updates, and DLC) is decrypted and extracted natively into RPCS3's `dev_hdd0/game/<TITLE_ID>` (under the selected emulator's install directory). Updates are applied in ascending `APP_VER` order. RAP licenses are copied into `dev_hdd0/home/00000001/exdata`.

Both halves are handled in-process — RPCS3 is not invoked to install anything. Uninstalling removes the copied disc image/folder and/or the `dev_hdd0/game/<TITLE_ID>` directory; save data (which RPCS3 stores elsewhere) is left untouched.

Game names and other details are enriched from an online database where possible — see [Metadata Enrichment](#metadata-enrichment).

### Known Issues (all types)

* If the connection to the source folder's storage is unstable, Playnite may crash when when updating the library. This is unlikely to be able to be completely fixed until Playnite uses a newer .NET version (currently being targeted for Playnite 11). Some some mitigations are planned in the meantime, but are not yet implemented.
* If the mapping is disabled or if EmuLibrary update is cancelled before the scan for the mapping completes, game installation for the mapping's games may result in an error message. This will be fixed in a later version of this addon.

## Metadata Enrichment

For some RomTypes, EmuLibrary looks up game details from a community metadata database and uses them in preference to whatever it can read out of the game files themselves. This generally yields cleaner, properly-formatted game names plus extra details that aren't present in the files at all.

| RomType | Provider | Matched by |
| --- | --- | --- |
| Yuzu (Switch) | [titledb](https://github.com/blawar/titledb) | Title ID |
| PS3 | [GameTDB](https://www.gametdb.com) | Disc serial (e.g. `BLES01234`) |

When a match is found, these fields are filled in (shadowing anything derived from the files): **name**, **description**, **developer**, **publisher**, **genres**, and **release date**. Anything the provider doesn't supply falls back to what was read from the files, so you never end up worse off than without enrichment.

A few notes:

* **Language** follows your Playnite language setting where the provider offers a translation, falling back to English otherwise.
* **Caching / offline use** — each provider's database is downloaded once and cached under `ExtensionsData\41e49490-0583-4148-94d2-940c7c74f1d9\metadata`, and only re-downloaded when the cached copy is more than a day old. If a provider can't be reached, the most recent cached copy is used, and if there's no cached copy at all, enrichment is simply skipped — it never blocks or fails a library scan.
* **Applies going forward** — because of how Playnite imports library games, enrichment primarily affects games as they are newly added. Re-applying it to games already in your library is not currently guaranteed.
* GameTDB also lists Switch games, but it keys them by a cartridge serial that loose NSP/XCI dumps don't contain, so it can't be used for the Yuzu type — titledb is used there instead.

## Support

To get help, check out the #extension-support channel on the Playnite Discord, linked at the top of https://playnite.link/

The following files are generally useful for troubleshooting, relative to the folder where Playnite data is stored. For a portable installation, this is the same folder that Playnite is installed to. For non-portable installations, it is in AppData.

* playnite.log
* extensions.log
* library\emulators.db
* library\platforms.db
* ExtensionsData\41e49490-0583-4148-94d2-940c7c74f1d9\config.json