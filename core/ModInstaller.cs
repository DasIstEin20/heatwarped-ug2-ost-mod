using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HeatwarpedUG2;

public sealed class InstallationRecord
{
    public string ModId { get; set; } = ModInfo.Id;
    public string Version { get; set; } = ModInfo.Version;
    public string InstalledAt { get; set; } = DateTimeOffset.Now.ToString("o");
    public string VersionDllSha256 { get; set; } = "";
    public int TrackCount { get; set; }
    public string? PreviousFolder { get; set; }
    public string? MixerSha256 { get; set; }
    public string? ShortcutSha256 { get; set; }
    public Dictionary<string,double> VolumeBefore { get; set; } = [];
    public Dictionary<string,double> VolumeApplied { get; set; } = [];
}
public sealed class ModInstaller(byte[] nativeLoader,string applicationPath,string notices)
{
    private static readonly string[] MusicGuids=[
        "32e4485e5dec43429e3394c12f5796be","3e7c9e9c6af6bb42a93493edb780550a","4b732ef107c3064aaf231b3f3ec5126a",
        "8fa00fe3acf871459c9bb523c3595120","c87d36ed8bd3c34690bd6b7bc3bef4d5","e6f4cb7a742a184290de558fa61374dc",
        "e908abab50b18444aa542a9470f571be","ebb9c8ebfa64f04fb937bd2ca6cc63ee","f7ba33afb7ee5f4da999978b1c018fd0"];
    private static readonly string[] FmodExports=["FMOD_Studio_System_Update","FMOD_Studio_EventInstance_Start","FMOD_Studio_EventInstance_Stop",
        "FMOD_Studio_EventInstance_SetVolume","FMOD_Studio_EventDescription_CreateInstance","FMOD_Studio_EventInstance_GetDescription",
        "FMOD_Studio_EventDescription_GetID","FMOD_Studio_System_GetCoreSystem","FMOD_System_CreateStream","FMOD_System_PlaySound",
        "FMOD_Studio_System_GetEventByID","FMOD_Studio_EventDescription_GetInstanceList","FMOD_Studio_Bus_LockChannelGroup"];
    public static GameInfo InspectGame(string folder,bool requireCompatibleAudio=true)
    {
        string root=Path.GetFullPath(folder.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar);
        string exe=Path.Combine(root,"Heatwarped.exe"),data=Path.Combine(root,"Heatwarped_Data");
        foreach(string path in new[]{exe,Path.Combine(root,"GameAssembly.dll"),Path.Combine(root,"UnityPlayer.dll"),Path.Combine(data,"app.info")})
            if(!File.Exists(path))throw new InvalidDataException("Select the Heatwarped Demo folder containing Heatwarped.exe and Heatwarped_Data.");
        byte[] exeBytes=File.ReadAllBytes(exe);
        if(exeBytes.Length<128||exeBytes[0]!='M'||exeBytes[1]!='Z')throw new InvalidDataException("Invalid Heatwarped executable.");
        int pe=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.AsSpan(0x3c,4)));
        if(pe<0||pe>exeBytes.Length-6||BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.AsSpan(pe,4))!=0x4550||BinaryPrimitives.ReadUInt16LittleEndian(exeBytes.AsSpan(pe+4,2))!=0x8664)
            throw new InvalidDataException("This mod requires the Windows 64-bit Heatwarped Demo.");
        string dll=Path.Combine(data,"Plugins","x86_64","fmodstudio.dll"),bank=Path.Combine(data,"StreamingAssets","Master.bank");
        if(requireCompatibleAudio && (!File.Exists(dll)||!File.Exists(bank)))throw new InvalidDataException("The FMOD audio engine or Master.bank is missing. Verify the demo installation first.");
        string fmodVersion="Unavailable";
        if(requireCompatibleAudio)
        {
            var version=FileVersionInfo.GetVersionInfo(dll);fmodVersion=version.FileVersion??"2.02";
            // FMOD stores 2.02.11 as 0,2,2,11 in the fixed file-version resource.
            bool supported=(version.FileMajorPart==2&&version.FileMinorPart==2)||
                (version.FileMajorPart==0&&version.FileMinorPart==2&&version.FileBuildPart==2);
            if(!supported)throw new InvalidDataException($"Unsupported FMOD version: {version.FileVersion}. This release supports FMOD 2.02.x.");
            byte[] api=File.ReadAllBytes(dll),events=File.ReadAllBytes(bank);
            if(FmodExports.Any(name=>api.AsSpan().IndexOf(Encoding.ASCII.GetBytes(name+"\0"))<0)||MusicGuids.Any(id=>events.AsSpan().IndexOf(Convert.FromHexString(id))<0))
                throw new InvalidDataException("This demo audio layout is not supported by this mod version. No files have been changed.");
        }
        string[] app=File.ReadAllLines(Path.Combine(data,"app.info"));
        if(app.Length<2||app.Take(2).Any(s=>s.Length==0||s is "." or ".."||s.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new InvalidDataException("Invalid game app.info.");
        string options=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"AppData","LocalLow",app[0],app[1],"Options");
        return new GameInfo(root,options,fmodVersion);
    }
    public async Task InstallAsync(GameInfo game,ExtractionResult extracted,IProgress<ProgressUpdate>? progress,CancellationToken token)
    {
        // Revalidate immediately before writing; a folder picker or earlier check is not a commit.
        game=InspectGame(game.Root);EnsureGameClosed(game.Root);
        string custom=Path.Combine(game.Root,"CustomMusic"),dll=Path.Combine(game.Root,"version.dll");
        var previous=ReadOwnedInstallation(game.Root);
        if(Directory.Exists(custom)&&previous==null)throw new IOException("CustomMusic already exists and is not owned by this mod. Move it aside before installing.");
        if(File.Exists(dll))
        {
            if(previous==null||!string.Equals(await SoundtrackExtractor.HashAsync(dll,token),previous.VersionDllSha256,StringComparison.OrdinalIgnoreCase))
                throw new IOException("version.dll belongs to another mod or has been modified. It will not be overwritten.");
        }
        if(Directory.Exists(custom)&&(File.GetAttributes(custom)&FileAttributes.ReparsePoint)!=0)throw new IOException("CustomMusic must be a real folder, not a link to another directory.");
        long required=extracted.Library.Tracks.Sum(t=>new FileInfo(Path.Combine(extracted.Folder,t.File)).Length)+new FileInfo(applicationPath).Length+64*1024*1024;
        if(new DriveInfo(Path.GetPathRoot(game.Root)!).AvailableFreeSpace<required)throw new IOException("There is not enough free space on the game drive (about 1.2 GB is required).");
        string work=OwnedWork.Create(game.Root),staged=Path.Combine(work,"CustomMusic");Directory.CreateDirectory(Path.Combine(staged,"Tracks"));
        string? backup=null;byte[]? originalOptions=null;bool swapped=false,dllTouched=false,optionsTouched=false;
        string shortcut=Path.Combine(game.Root,"Music Settings.cmd");byte[]? originalShortcut=File.Exists(shortcut)?await File.ReadAllBytesAsync(shortcut,token):null;
        string? oldDllBackup=null;
        try
        {
            int complete=0;
            foreach(var track in extracted.Library.Tracks)
            {
                token.ThrowIfCancellationRequested();string source=Path.Combine(extracted.Folder,track.File),dest=SafeInside(staged,track.File);
                if(!string.Equals(await SoundtrackExtractor.HashAsync(source,token),track.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("A converted track changed after verification. Please convert it again.");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);await CopyFileAsync(source,dest,token);
                progress?.Report(new ProgressUpdate(++complete*75/43,$"Installing track {complete}/43: {track.Artist} — {track.Title}"));
            }
            var playlist=Playlist.FromLibrary(extracted.Library);
            if(previous!=null)
            {
                playlist.MergePrevious(custom,extracted.Library);
                // Keep additional user tracks available after an upgrade. Never follow directory links.
                string oldTracks=Path.Combine(custom,"Tracks");
                if(Directory.Exists(oldTracks))
                {
                    var convertedFiles=extracted.Library.Tracks.Select(t=>t.File.Replace('/',Path.DirectorySeparatorChar)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach(string file in EnumeratePlainFiles(oldTracks))
                    {
                        string relative=Path.GetRelativePath(custom,file);
                        if(convertedFiles.Contains(relative))continue;
                        string dest=SafeInside(staged,relative);if(File.Exists(dest))continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);await CopyFileAsync(file,dest,token);
                    }
                }
                string oldBackup=Path.Combine(custom,"backup");
                if(Directory.Exists(oldBackup))foreach(string file in EnumeratePlainFiles(oldBackup))
                {
                    string dest=SafeInside(staged,Path.GetRelativePath(custom,file));Directory.CreateDirectory(Path.GetDirectoryName(dest)!);await CopyFileAsync(file,dest,token);
                }
            }
            await File.WriteAllTextAsync(Path.Combine(staged,"library.json"),JsonSerializer.Serialize(extracted.Library,ModInfo.Json),Encoding.UTF8,token);
            playlist.Save(Path.Combine(staged,"playlists.ini"));
            await CopyFileAsync(applicationPath,Path.Combine(staged,"MusicSettings.exe"),token);
            await File.WriteAllTextAsync(Path.Combine(staged,"THIRD-PARTY-NOTICES.txt"),notices,Encoding.UTF8,token);
            await File.WriteAllTextAsync(Path.Combine(staged,"README.txt"),InstalledReadme,Encoding.UTF8,token);
            await File.WriteAllTextAsync(Path.Combine(staged,"Uninstall Mod.cmd"),"@echo off\r\nstart \"\" \"%~dp0MusicSettings.exe\" --uninstall\r\n",Encoding.ASCII,token);
            var record=new InstallationRecord {VersionDllSha256=Convert.ToHexString(SHA256.HashData(nativeLoader)).ToLowerInvariant(),
                MixerSha256=await SoundtrackExtractor.HashAsync(Path.Combine(staged,"MusicSettings.exe"),token),TrackCount=extracted.Library.Tracks.Count};
            if(previous!=null){record.VolumeBefore=previous.VolumeBefore;record.VolumeApplied=previous.VolumeApplied;}
            Directory.CreateDirectory(Path.Combine(staged,"backup"));
            JsonObject? options=null;
            if(File.Exists(game.OptionsPath))
            {
                originalOptions=await File.ReadAllBytesAsync(game.OptionsPath,token);
                options=JsonNode.Parse(originalOptions)?.AsObject()??throw new InvalidDataException("The game audio options file is invalid.");
                string savedOptions=Path.Combine(staged,"backup","Options.before-install.json");
                if(!File.Exists(savedOptions))await File.WriteAllBytesAsync(savedOptions,originalOptions,token);
                foreach(string key in new[]{"MenuMusicVolume","GameMusicVolume"})
                    if(options[key]?.GetValue<double>()==0){record.VolumeBefore.TryAdd(key,0);record.VolumeApplied[key]=.7;options[key]=.7;}
            }
            string shortcutBody="@echo off\r\nstart \"\" \"%~dp0CustomMusic\\MusicSettings.exe\"\r\n";
            record.ShortcutSha256=Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(shortcutBody))).ToLowerInvariant();
            if(originalShortcut!=null && (previous?.ShortcutSha256==null||!string.Equals(Convert.ToHexString(SHA256.HashData(originalShortcut)),previous.ShortcutSha256,StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Music Settings.cmd already exists and is not owned by this installer.");
            token.ThrowIfCancellationRequested();EnsureGameClosed(game.Root);
            progress?.Report(new ProgressUpdate(90,"Applying the mod. Existing settings and a rollback copy are preserved…"));
            if(Directory.Exists(custom))
            {
                backup=Path.Combine(game.Root,"CustomMusic.backup-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);
                record.PreviousFolder=Path.GetFileName(backup);
            }
            await File.WriteAllTextAsync(Path.Combine(staged,"installation.json"),JsonSerializer.Serialize(record,ModInfo.Json),Encoding.UTF8,CancellationToken.None);
            // The short commit phase is deliberately not cancellable. Each mutation has a rollback.
            if(backup!=null)Directory.Move(custom,backup);
            Directory.Move(staged,custom);swapped=true;
            if(File.Exists(dll)){oldDllBackup=Path.Combine(work,"version.previous.dll");File.Move(dll,oldDllBackup);}
            string newDll=Path.Combine(work,"version.new.dll");await File.WriteAllBytesAsync(newDll,nativeLoader);File.Move(newDll,dll);dllTouched=true;
            File.WriteAllText(shortcut,shortcutBody,Encoding.ASCII);
            if(options!=null && record.VolumeApplied.Count>0){AtomicWrite(game.OptionsPath,Encoding.UTF8.GetBytes(options.ToJsonString(ModInfo.Json)));optionsTouched=true;}
            if(oldDllBackup!=null)File.Move(oldDllBackup,Path.Combine(custom,"backup","version.previous.dll"),true);
            progress?.Report(new ProgressUpdate(100,"Installed. F8 opens Music Settings; F9 skips to the next track."));
        }
        catch
        {
            if(optionsTouched&&originalOptions!=null)AtomicWrite(game.OptionsPath,originalOptions);
            if(dllTouched&&File.Exists(dll))File.Move(dll,Path.Combine(work,"version.failed.dll"),true);
            if(oldDllBackup!=null&&File.Exists(oldDllBackup))File.Move(oldDllBackup,dll,true);
            if(swapped&&Directory.Exists(custom))Directory.Move(custom,Path.Combine(work,"failed-custom"));
            if(backup!=null&&Directory.Exists(backup)&&!Directory.Exists(custom))Directory.Move(backup,custom);
            if(swapped){if(originalShortcut!=null)AtomicWrite(shortcut,originalShortcut);else if(File.Exists(shortcut))File.Delete(shortcut);}
            throw;
        }
        finally {try{OwnedWork.Delete(work,game.Root);}catch(IOException){} }
    }
    public static InstallationRecord? ReadOwnedInstallation(string gameRoot)
    {
        string file=Path.Combine(gameRoot,"CustomMusic","installation.json");if(!File.Exists(file))return null;
        using var doc=JsonDocument.Parse(File.ReadAllText(file,Encoding.UTF8));
        bool modern=doc.RootElement.TryGetProperty("modId",out var id)&&id.GetString()==ModInfo.Id;
        bool legacy=doc.RootElement.TryGetProperty("source",out var source)&&source.GetString()=="Local NFS Underground 2 / SDATA/sdat.viv";
        if(!modern&&!legacy)return null;
        return JsonSerializer.Deserialize<InstallationRecord>(doc.RootElement.GetRawText(),ModInfo.Json);
    }
    public static string Uninstall(string folder)
    {
        var game=InspectGame(folder,false);EnsureGameClosed(game.Root);var record=ReadOwnedInstallation(game.Root)??throw new IOException("This folder has no installation owned by this mod.");
        string custom=Path.Combine(game.Root,"CustomMusic"),dll=Path.Combine(game.Root,"version.dll");
        if(File.Exists(dll)&&!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))),record.VersionDllSha256,StringComparison.OrdinalIgnoreCase))throw new IOException("version.dll has been changed by another mod and will not be removed.");
        if((File.GetAttributes(custom)&FileAttributes.ReparsePoint)!=0)throw new IOException("Refusing to move a linked CustomMusic folder.");
        // An uninstaller started from this folder otherwise holds a directory
        // handle that prevents Windows from renaming it, including its parent.
        string cwd=Environment.CurrentDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if(cwd.Equals(custom,StringComparison.OrdinalIgnoreCase)||cwd.StartsWith(custom+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            Environment.CurrentDirectory=game.Root;
        string disabled=Path.Combine(game.Root,"CustomMusic.disabled-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);
        Directory.Move(custom,disabled);
        try {if(File.Exists(dll))File.Move(dll,Path.Combine(disabled,"version.disabled.dll"));}
        catch {Directory.Move(disabled,custom);throw;}
        if(File.Exists(game.OptionsPath)&&record.VolumeApplied.Count>0)
        {
            var options=JsonNode.Parse(File.ReadAllBytes(game.OptionsPath))!.AsObject();
            foreach(var entry in record.VolumeApplied)if(options[entry.Key]?.GetValue<double>()==entry.Value&&record.VolumeBefore.TryGetValue(entry.Key,out double before))options[entry.Key]=before;
            AtomicWrite(game.OptionsPath,Encoding.UTF8.GetBytes(options.ToJsonString(ModInfo.Json)));
        }
        string shortcut=Path.Combine(game.Root,"Music Settings.cmd");
        if(File.Exists(shortcut)&&record.ShortcutSha256!=null&&string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(shortcut))),record.ShortcutSha256,StringComparison.OrdinalIgnoreCase))File.Delete(shortcut);
        return disabled;
    }
    private static void EnsureGameClosed(string root)
    {
        foreach(var p in Process.GetProcesses())
        {
            using(p)
            {
                if(p.Id==Environment.ProcessId||p.ProcessName is not ("Heatwarped" or "MusicSettings"))continue;
                try {string? file=p.MainModule?.FileName;if(file!=null&&file.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Close Heatwarped Demo and its Music Settings window before installing or uninstalling.");}
                catch(System.ComponentModel.Win32Exception){}
            }
        }
    }
    private static IEnumerable<string> EnumeratePlainFiles(string directory)
    {
        if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("A user music or backup folder is a directory link. Move it aside before upgrading.");
        foreach(string file in Directory.EnumerateFiles(directory))
        {
            if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new IOException("A user music file is a link. Move it aside before upgrading.");
            yield return file;
        }
        foreach(string child in Directory.EnumerateDirectories(directory))foreach(string file in EnumeratePlainFiles(child))yield return file;
    }
    private static string SafeInside(string parent,string relative)
    {
        string result=Path.GetFullPath(Path.Combine(parent,relative));
        if(!result.StartsWith(Path.GetFullPath(parent)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("A music path escapes the mod folder.");
        return result;
    }
    private static async Task CopyFileAsync(string source,string destination,CancellationToken token)
    {
        await using var input=File.OpenRead(source);await using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024,true);await input.CopyToAsync(output,token);
    }
    private static void AtomicWrite(string path,byte[] bytes)
    {
        string temp=path+".ug2-"+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temp,bytes);File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public const string InstalledReadme="""
Heatwarped UG2 OST Mod

Launch Heatwarped Demo normally. F8 toggles Music Settings inside the game;
F8, Escape or X closes it. Drag the header to move it and scroll through the
songs. Select Menu and/or Driving for each song, adjust volume and shuffle.
Changes save automatically. The game pauses while the panel is open and its
previous pause state is restored when you close the panel. F9 skips a song.

Every new custom song while driving shows an UG2-style notification at the
top left for four seconds, fading out over the last 0.8 seconds. Race starts
and restarts advance the driving playlist once at the race countdown.

Default UG2 split: 6 menu tracks, 21 driving tracks; 16 alternate versions are
available to select. An empty playlist or disabling custom music restores the
original soundtrack. The game audio sliders and master volume still apply.

Music Settings.cmd opens the offline Windows utility for importing WAV, MP3,
OGG, FLAC and AIFF files or previewing songs. F8 always opens the in-game
panel. The original Jukebox still lists the original Heatwarped tracks.

This mod contains no NFS music or original game binaries in its distribution.
Audio is extracted locally from the user's Underground 2 installation.
Uninstall Mod.cmd disables the loader and preserves the music in a disabled
folder. Existing installations are preserved in CustomMusic.backup-* folders.

Tested: Heatwarped Demo b957f2bca, Unity 6000.4.1f1, FMOD 2.02.11, Windows x64.
""";
}

internal sealed class Playlist
{
    public sealed record Choice(string File,string Title,bool Menu,bool Drive);
    public List<Choice> Tracks { get; }=[];
    private bool enabled=true,shuffle=true;
    private double gain=.8;
    public static Playlist FromLibrary(MusicLibrary library)
    {
        var p=new Playlist();p.Tracks.AddRange(library.Tracks.Select(t=>new Choice(t.File.Replace('/',Path.DirectorySeparatorChar),t.Artist+" - "+t.Title,t.Menu,t.Drive)));return p;
    }
    public void MergePrevious(string custom,MusicLibrary library)
    {
        string ini=Path.Combine(custom,"playlists.ini");if(!File.Exists(ini))return;
        var sections=new Dictionary<string,Dictionary<string,string>>(StringComparer.OrdinalIgnoreCase);Dictionary<string,string>? current=null;
        foreach(string raw in File.ReadAllLines(ini,Encoding.Unicode))
        {
            string line=raw.Trim();if(line.StartsWith('[')&&line.EndsWith(']')){sections[line[1..^1]]=current=new(StringComparer.OrdinalIgnoreCase);continue;}
            int at=line.IndexOf('=');if(current!=null&&at>0)current[line[..at]]=line[(at+1)..];
        }
        if(sections.TryGetValue("General",out var general))
        {
            enabled=general.GetValueOrDefault("Enabled","1")=="1";shuffle=general.GetValueOrDefault("Shuffle","1")=="1";
            if(double.TryParse(general.GetValueOrDefault("Gain","0.8"),CultureInfo.InvariantCulture,out double g))gain=Math.Clamp(g,0,1);
        }
        var oldLibrary=JsonSerializer.Deserialize<MusicLibrary>(File.ReadAllText(Path.Combine(custom,"library.json")),ModInfo.Json);
        foreach(var section in sections.Where(s=>s.Key.StartsWith("Track",StringComparison.OrdinalIgnoreCase)).Select(s=>s.Value))
        {
            string file=section.GetValueOrDefault("File","").Replace('/',Path.DirectorySeparatorChar);if(file.Length==0)continue;
            bool menu=section.GetValueOrDefault("Menu","0")=="1",drive=section.GetValueOrDefault("Drive","0")=="1";
            int stream=oldLibrary?.Tracks.Find(t=>string.Equals(t.File.Replace('/',Path.DirectorySeparatorChar),file,StringComparison.OrdinalIgnoreCase))?.SourceStream??0;
            string mapped=library.Tracks.Find(t=>stream>0&&t.SourceStream==stream)?.File.Replace('/',Path.DirectorySeparatorChar)??file;
            int index=Tracks.FindIndex(t=>string.Equals(t.File,mapped,StringComparison.OrdinalIgnoreCase));
            if(index>=0)Tracks[index]=Tracks[index] with{Menu=menu,Drive=drive};
            else
            {
                if(!Path.IsPathRooted(file))SafeRelative(file);
                Tracks.Add(new Choice(file,section.GetValueOrDefault("Title",Path.GetFileNameWithoutExtension(file)),menu,drive));
            }
        }
        if(Tracks.Count>1024)throw new InvalidDataException("This version supports a maximum of 1024 music entries.");
    }
    private static void SafeRelative(string file){if(!file.StartsWith("Tracks"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||file.Split(Path.DirectorySeparatorChar).Any(p=>p==".."))throw new InvalidDataException("An existing playlist contains an unsafe relative track path.");}
    public void Save(string path)
    {
        var s=new StringBuilder().AppendLine("[General]").AppendLine($"Enabled={(enabled?1:0)}").AppendLine($"Shuffle={(shuffle?1:0)}").AppendLine("Gain="+gain.ToString("0.00",CultureInfo.InvariantCulture)).AppendLine($"Count={Tracks.Count}");
        for(int i=0;i<Tracks.Count;i++){var t=Tracks[i];s.AppendLine().AppendLine($"[Track{i}]").AppendLine("File="+t.File.Replace('\r',' ').Replace('\n',' ')).AppendLine("Title="+t.Title.Replace('\r',' ').Replace('\n',' ')).AppendLine($"Menu={(t.Menu?1:0)}").AppendLine($"Drive={(t.Drive?1:0)}");}
        File.WriteAllText(path,s.ToString(),Encoding.Unicode);
    }
}
