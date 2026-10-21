using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeatwarpedMusic;
using HeatwarpedUG2;

namespace HeatwarpedUG2Setup;

internal static class Program
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr FindWindow(string? cls,string title);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")]private static extern bool ShowWindow(IntPtr handle,int command);
    public static byte[] Loader()
    {
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.version.dll")??throw new IOException("The native loader payload is missing.");
        using var output=new MemoryStream();stream.CopyTo(output);return output.ToArray();
    }
    public static string Notices()
    {
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.notices.txt")!;
        using var reader=new StreamReader(stream);return reader.ReadToEnd();
    }
    public static bool RelocateUninstaller(string gameFolder)
    {
        string game=Path.GetFullPath(gameFolder.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar);
        string custom=Path.Combine(game,"CustomMusic"),exe=Environment.ProcessPath!;
        string own=Path.GetDirectoryName(exe)!;
        if(!own.Equals(custom,StringComparison.OrdinalIgnoreCase)&&!own.StartsWith(custom+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return false;
        // Windows locks the directory containing a running executable. Start
        // the same verified program outside the mod before moving its folder.
        string hash;using(var input=File.OpenRead(exe))hash=Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        string cache=Path.Combine(Path.GetTempPath(),"HeatwarpedUG2Mod","uninstallers",hash);
        Directory.CreateDirectory(cache);string helper=Path.Combine(cache,"HeatwarpedUG2Mod.exe");
        bool valid=false;
        if(File.Exists(helper)){using var input=File.OpenRead(helper);valid=Convert.ToHexString(SHA256.HashData(input)).Equals(hash,StringComparison.OrdinalIgnoreCase);}
        if(!valid)
        {
            string staging=Path.Combine(cache,"copy-"+Guid.NewGuid().ToString("N")+".tmp");
            try{File.Copy(exe,staging);File.Move(staging,helper,true);}finally{if(File.Exists(staging))File.Delete(staging);}
        }
        var start=new ProcessStartInfo(helper){UseShellExecute=false,WorkingDirectory=Path.GetTempPath()};
        foreach(string arg in new[]{"--uninstall",game,"--wait-for-exit",Environment.ProcessId.ToString()})start.ArgumentList.Add(arg);
        using var child=Process.Start(start)??throw new IOException("Unable to start the temporary uninstaller.");
        return true;
    }
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string exe=Environment.ProcessPath??throw new IOException("Cannot locate this application.");
        string ownFolder=Path.GetDirectoryName(exe)!;
        try
        {
            int wait=Array.IndexOf(args,"--wait-for-exit");
            if(wait>=0&&wait+1<args.Length&&int.TryParse(args[wait+1],out int parentId)&&parentId!=Environment.ProcessId)
            {
                try{using var parent=Process.GetProcessById(parentId);if(!parent.WaitForExit(20000))throw new IOException("Close the previous Music Settings window and try uninstalling again.");}
                catch(ArgumentException){} // The launcher has already exited.
            }
            if(args.Length>0&&args[0]=="--verify-package")
            {
                string output=args.Length>1?args[1]:Path.Combine(ownFolder,"package-check.json");
                byte[] loader=Loader();
                File.WriteAllText(output,JsonSerializer.Serialize(new{version=ModInfo.Version,architecture=RuntimeInformation.ProcessArchitecture.ToString(),
                    embeddedResources=Assembly.GetExecutingAssembly().GetManifestResourceNames(),nativeLoaderSha256=Convert.ToHexString(SHA256.HashData(loader)),
                    musicBundled=false,selfContained=true},ModInfo.Json));return 0;
            }
            if(args.Length>0&&args[0]=="--check")
            {
                string root=args.Length>1?Path.GetFullPath(args[1]):ownFolder;var store=new SettingsStore(root);store.Load();
                if(store.Tracks.Count==0||store.Tracks.Any(t=>!File.Exists(Path.Combine(root,t.File))))return 2;
                File.WriteAllText(Path.Combine(root,"settings-check.json"),JsonSerializer.Serialize(new{count=store.Tracks.Count,menu=store.Tracks.Count(t=>t.Menu),drive=store.Tracks.Count(t=>t.Drive),enabled=store.Enabled},ModInfo.Json));return 0;
            }
            if(args.Length>0&&args[0]=="--uninstall")
            {
                string game=args.Length>1?Path.GetFullPath(args[1]):Path.GetDirectoryName(ownFolder)!;
                if(RelocateUninstaller(game))return 0;
                Application.Run(new SetupForm(game,true));return 0;
            }
            if(Path.GetFileName(exe).Equals("MusicSettings.exe",StringComparison.OrdinalIgnoreCase)&&!args.Contains("--setup"))
            {
                string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownFolder)))[..16];
                using var mutex=new Mutex(true,"Local\\HeatwarpedMusicSettings_"+key,out bool fresh);
                if(!fresh){var h=FindWindow(null,"Heatwarped - Music Settings");ShowWindow(h,9);SetForegroundWindow(h);return 0;}
                Application.Run(new SettingsForm(ownFolder));return 0;
            }
            Application.Run(new SetupForm());return 0;
        }
        catch(Exception e){MessageBox.Show(e.Message,"Heatwarped UG2 OST Mod",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
    }
}
