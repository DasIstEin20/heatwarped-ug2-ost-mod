Heatwarped UG2 OST Mod 1.1.0 — Windows x64

1. Run HeatwarpedUG2Mod.exe.
2. Choose the folder containing your NFS Underground 2 SPEED2.EXE.
3. Click Extract soundtrack. The first run downloads the official vgmstream
   r2117 decoder (4.5 MB); subsequent runs can use the verified cached ZIP.
4. Choose your installed Heatwarped Demo folder and click Install mod.
5. Launch the game normally. Press F8 for Music Settings, F9 for the next song.

Music Settings is entirely in English. Every song has separate Menu and
Driving / racing checkboxes. The default split is 6 menu tracks, 21 driving
tracks, and 16 optional alternate versions. Settings save automatically.

The installer is self-contained: no Python, FFmpeg command-line installation,
or separately installed .NET runtime is required. Internet access is needed
only to fetch the decoder on the first conversion. For offline installation,
copy the original, unchanged vgmstream-win64.zip for release r2117 to:
  %LOCALAPPDATA%\HeatwarpedUG2Mod\Downloads\vgmstream-r2117-win64.zip
The installer verifies its pinned SHA-256 before using it.

Required: your own installed PC copy of NFS Underground 2, the Windows x64
Heatwarped Demo, about 1.2 GB of temporary space and 1.2 GB on the game drive.
Supported extraction layout: original PC Pathfinder v4, 43 streams.
Tested game: demo b957f2bca / Unity 6000.4.1f1 / FMOD 2.02.11. Other versions
must pass the installer audio-layout and FMOD checks. Future builds may need
a mod update.

Close the game and its Music Settings window before installation. If Windows
denies access to a protected game folder, run the installer as administrator.
An existing unrelated version.dll is never overwritten.

The distribution contains no soundtrack, NFS archive, Heatwarped executable,
or FMOD engine binary. Music is decoded locally to 32 kHz stereo PCM16 WAV.
No source NFS files are modified. The game stays in the folder you select;
the installer does not move it or create Steam directory links.

Updates preserve playlist choices and additional user music. An existing mod
folder is retained as CustomMusic.backup-*. To disable/uninstall, run Setup
and choose Uninstall an existing mod, or use CustomMusic\Uninstall Mod.cmd.
Music is preserved in a CustomMusic.disabled-* folder; no saves are deleted.

The original in-game Jukebox still shows the original Heatwarped titles.
Use F8 or Music Settings.cmd for the UG2 music configuration.

Redistribution: share this installer/ZIP, not the extracted music folder.
See LICENSE and THIRD-PARTY-NOTICES.txt. This is an unofficial fan mod and is
not affiliated with the developers of either game.
