using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeatwarpedUG2;

internal sealed class InlineProgress(Action<ProgressUpdate> callback):IProgress<ProgressUpdate>{public void Report(ProgressUpdate p)=>callback(p);}
internal static class Program
{
    static void Require(bool condition,string name){if(!condition)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name);}
    static async Task Reject(Func<Task> action,string name){try{await action();}catch(Exception e)when(e is IOException or InvalidDataException or OperationCanceledException){Console.WriteLine("PASS: "+name);return;}throw new Exception("FAIL: "+name);}
    static async Task<int> Main(string[] args)
    {
        if(args.Length!=5){Console.WriteLine("Usage: CoreTests <NFS UG2> <Heatwarped Demo> <vgmstream ZIP> <published setup EXE> <test output parent>");return 2;}
        string nfs=args[0],game=args[1],zip=args[2],app=args[3],parent=Path.GetFullPath(args[4]);Directory.CreateDirectory(parent);
        string work=OwnedWork.Create(parent),reports=Path.Combine(parent,"test-results.json");var results=new List<string>();
        string? testOptions=null;string originalCwd=Environment.CurrentDirectory;
        try
        {
            await Reject(()=>Task.Run(()=>SoundtrackExtractor.Inspect(work)),"reject missing source install");
            var source=SoundtrackExtractor.Inspect(nfs);
            Require(source.Tracks.Count==43&&source.Tracks.Count(t=>t.Menu)==6&&source.Tracks.Count(t=>t.Drive)==21&&source.Tracks.Count(t=>t.Alternate)==16,"discover all songs and original UG2 categories without executable offsets");
            string sourceHash=await SoundtrackExtractor.HashAsync(source.ArchivePath);
            var extractor=new SoundtrackExtractor();
            using(var stopped=new CancellationTokenSource()){stopped.Cancel();await Reject(async()=>await extractor.ExtractAsync(source,work,null,stopped.Token,zip),"cancel conversion before launching decoder");}
            var extracted=await extractor.ExtractAsync(source,work,new InlineProgress(p=>{if(p.Percent%10==0)Console.WriteLine(p.Message);}),CancellationToken.None,zip);
            Require(extracted.Library.Tracks.All(t=>t.DurationSeconds>30&&t.Sha256.Length==64),"decode and validate all 43 PCM16 WAV tracks");
            string root=Path.Combine(work,"Heatwarped test — ścieżka ąę");Directory.CreateDirectory(root);
            foreach(string name in new[]{"Heatwarped.exe","GameAssembly.dll","UnityPlayer.dll"})File.Copy(Path.Combine(game,name),Path.Combine(root,name));
            foreach(string relative in new[]{"Plugins/x86_64/fmodstudio.dll","StreamingAssets/Master.bank","StreamingAssets/Master.strings.bank"})
            {
                string dest=Path.Combine(root,"Heatwarped_Data",relative);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Copy(Path.Combine(game,"Heatwarped_Data",relative),dest);
            }
            string product="UG2ModTest-"+Guid.NewGuid().ToString("N");File.WriteAllText(Path.Combine(root,"Heatwarped_Data","app.info"),"HeatwarpedModTests\n"+product);
            var target=ModInstaller.InspectGame(root);testOptions=target.OptionsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(testOptions)!);File.WriteAllText(testOptions,"{\"MenuMusicVolume\":0,\"GameMusicVolume\":0,\"UnrelatedOption\":123}");
            byte[] loader=ReadLoaderFromBuild(app);
            var installer=new ModInstaller(loader,app,"Integration test notices");
            string dll=Path.Combine(root,"version.dll"),custom=Path.Combine(root,"CustomMusic");
            File.WriteAllText(dll,"another mod owns this proxy");
            await Reject(()=>installer.InstallAsync(target,extracted,null,CancellationToken.None),"refuse an unrelated version.dll");Require(File.ReadAllText(dll)=="another mod owns this proxy","preserve unrelated proxy bytes");File.Delete(dll);
            using(var cancel=new CancellationTokenSource())
            {
                await Reject(()=>installer.InstallAsync(target,extracted,new InlineProgress(p=>{if(p.Percent>=20)cancel.Cancel();}),cancel.Token),"cancel during staged install before commit");
                Require(!Directory.Exists(custom)&&!File.Exists(dll),"cancelled install leaves target unpatched");
            }
            await installer.InstallAsync(target,extracted,null,CancellationToken.None);
            Require(File.Exists(dll)&&File.Exists(Path.Combine(custom,"MusicSettings.exe")),"install portable loader and self-contained mixer");
            var check=new System.Diagnostics.ProcessStartInfo(Path.Combine(custom,"MusicSettings.exe")){UseShellExecute=false,CreateNoWindow=true};check.ArgumentList.Add("--check");check.ArgumentList.Add(custom);
            using(var p=System.Diagnostics.Process.Start(check)!){await p.WaitForExitAsync();Require(p.ExitCode==0,"installed standalone mixer loads all tracks without sidecar DLL files");}
            foreach(var track in extracted.Library.Tracks)RequireHash(Path.Combine(custom,track.File),track.Sha256);
            Require(true,"all installed track hashes match the verified conversion");
            string ini=Path.Combine(custom,"playlists.ini");string text=File.ReadAllText(ini,Encoding.Unicode).Replace("Shuffle=1","Shuffle=0").Replace("Gain=0.80","Gain=0.42").Replace("Count=43","Count=44");
            int section=text.IndexOf("[Track0]",StringComparison.Ordinal),menu=text.IndexOf("Menu=0",section,StringComparison.Ordinal);text=text.Remove(menu+5,1).Insert(menu+5,"1");
            File.Copy(Path.Combine(custom,extracted.Library.Tracks[0].File),Path.Combine(custom,"Tracks","user-added.wav"));
            text+="\r\n[Track43]\r\nFile=Tracks\\user-added.wav\r\nTitle=User track\r\nMenu=1\r\nDrive=0\r\n";File.WriteAllText(ini,text,Encoding.Unicode);
            await installer.InstallAsync(target,extracted,null,CancellationToken.None);
            string upgraded=File.ReadAllText(ini,Encoding.Unicode);
            Require(upgraded.Contains("Count=44")&&upgraded.Contains("Gain=0.42")&&upgraded.Contains("Shuffle=0")&&File.Exists(Path.Combine(custom,"Tracks","user-added.wav")),"upgrade preserves user tracks and playlist preferences");
            Require(Directory.EnumerateDirectories(root,"CustomMusic.backup-*").Any(),"upgrade retains a rollback copy");
            File.WriteAllText(dll,"unexpected replacement");await Reject(()=>Task.Run(()=>ModInstaller.Uninstall(root)),"uninstall protects a subsequently replaced proxy");await File.WriteAllBytesAsync(dll,loader);
            File.WriteAllText(testOptions,"{\"MenuMusicVolume\":0.7,\"GameMusicVolume\":0.9,\"UnrelatedOption\":123}");
            File.Move(Path.Combine(root,"Heatwarped_Data","StreamingAssets","Master.bank"),Path.Combine(root,"Heatwarped_Data","StreamingAssets","Master.bank.updated"));
            await Reject(()=>Task.Run(()=>ModInstaller.InspectGame(root)),"refuse installation when game audio files change");
            Environment.CurrentDirectory=Path.Combine(custom,"Tracks");
            string disabled=ModInstaller.Uninstall(root);
            Require(!File.Exists(dll)&&!Directory.Exists(custom)&&Directory.Exists(Path.Combine(disabled,"Tracks")),"uninstall works after a game audio update and preserves the music");
            Require(Environment.CurrentDirectory==root,"uninstall releases its own music folder before renaming it");
            using(var json=JsonDocument.Parse(File.ReadAllText(testOptions)))Require(json.RootElement.GetProperty("MenuMusicVolume").GetDouble()==0&&json.RootElement.GetProperty("GameMusicVolume").GetDouble()==.9&&json.RootElement.GetProperty("UnrelatedOption").GetInt32()==123,"restore only installer-changed volume values that the user has not changed");
            Require(await SoundtrackExtractor.HashAsync(source.ArchivePath)==sourceHash,"source NFS archive remains byte-for-byte unchanged");
            File.WriteAllText(reports,JsonSerializer.Serialize(new{passed=true,trackCount=43,sourceTracks=27,alternateTracks=16,unicodePaths=true,portableMixer=true,foreignModProtection=true,cancellationRollback=true,upgradePreservation=true,uninstall=true,sourceUnchanged=true,testRoot=root},ModInfo.Json));
            Console.WriteLine("Full integration test passed: "+reports);return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);File.WriteAllText(reports,JsonSerializer.Serialize(new{passed=false,error=e.ToString()},ModInfo.Json));return 1;}
        finally
        {
            Environment.CurrentDirectory=originalCwd;
            if(testOptions!=null&&File.Exists(testOptions)){File.Delete(testOptions);Directory.Delete(Path.GetDirectoryName(testOptions)!,false);}
            // Retain the owned fixture for review; its path is recorded in test-results.json.
            Console.WriteLine("Owned test fixture: "+work);
        }
    }
    private static void RequireHash(string path,string expected){if(!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),expected,StringComparison.OrdinalIgnoreCase))throw new Exception("Installed track changed: "+path);}
    private static byte[] ReadLoaderFromBuild(string app)
    {
        string path=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(app)!,"..","..","release-assets","version.dll"));
        if(!File.Exists(path))throw new IOException("Build native release-assets/version.dll before running tests.");return File.ReadAllBytes(path);
    }
}
