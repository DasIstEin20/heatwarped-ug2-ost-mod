# Heatwarped UG2 OST Mod

Import the **Need for Speed Underground 2** soundtrack into **Heatwarped Demo**, with separate menu and driving/racing playlists.

## What it does

- Extracts music from your installed PC copy of Underground 2 and converts it locally to WAV.
- Adds 27 full songs and 16 optional alternate versions. Defaults match UG2: 6 menu songs and 21 driving songs.
- Installs an audio loader and an English Music Settings panel. Each track can play in the menu, during free-roam/races, or in both playlists.
- Supports additional user music, shuffle, volume adjustment and changes while the game is running.

The download contains **no soundtrack or original game files**. You need your own installations of both games.

## Installation

1. Download and extract the Windows x64 ZIP from [Releases](https://github.com/DasIstEin20/heatwarped-ug2-ost-mod/releases/latest).
2. Close Heatwarped, then run `HeatwarpedUG2Mod.exe`.
3. Select your Underground 2 folder containing `SPEED2.EXE` and click **Extract soundtrack**.
4. Select your Heatwarped Demo folder and click **Install mod**.

Launch the game normally. **F8** opens Music Settings; **F9** skips to the next track. Your game audio sliders still apply.

No Python or separate .NET installation is required. The first conversion downloads the official vgmstream r2117 decoder (about 4.5 MB), verifies its SHA-256 and runs it locally. Allow about 1.2 GB of temporary space and 1.2 GB on the game drive. Offline setup instructions are in [the included guide](release-assets/README.txt).

Tested with Heatwarped Demo `b957f2bca`, Unity 6000.4.1f1 and FMOD 2.02.11. The installer checks the audio layout and refuses unsupported builds or an unrelated `version.dll`. The original in-game Jukebox keeps its original titles; use Music Settings to configure the UG2 tracks.

## Antivirus and VirusTotal

The mod uses an unsigned .NET executable that extracts runtime libraries, a temporary uninstaller helper, and a local `version.dll` proxy using MinHook to intercept FMOD audio calls inside the game process. These are **possible reasons for heuristic antivirus alerts**, not a confirmed explanation for any particular detection.

On 2026-10-21, a user-submitted [VirusTotal scan of the v1.1.0 ZIP](https://www.virustotal.com/gui/file/2e38feeb740ab41fe996267491a9daff677828018d4ee40e45f3e8e95054fe07/detection) showed **2/66 detections**, from Bkav Pro and Zillya. Neither detection has been confirmed as a false positive. A low detection count alone does not establish safety, and the functional tests are not an independent security audit.

The mod source implements no telemetry or startup persistence. Its only network download is the pinned decoder described above. You can inspect the source, compare the release checksum and scan the installer and loader separately. Suspected false positives should be [reported to the detecting vendors](https://docs.virustotal.com/docs/false-positive).

## Uninstall and build

Run Setup and choose **Uninstall an existing mod**, or run `CustomMusic\Uninstall Mod.cmd`. Music and settings are preserved in a `CustomMusic.disabled-*` folder. Updates preserve playlist choices and retain the previous mod folder as a backup.

For source builds and integration tests, see [BUILDING.txt](BUILDING.txt). Requirements: Windows x64, .NET 8 SDK, MinGW-w64 x64 GCC and PowerShell 7.

The mod code is under the [MIT license](LICENSE). See [third-party notices](release-assets/THIRD-PARTY-NOTICES.txt) for dependencies. This is an unofficial fan mod, unaffiliated with the developers of either game.
