using System.Text.Json;

namespace HeatwarpedUG2;

public static class ModInfo
{
    public const string Id = "Heatwarped.UG2.OST";
    public const string Version = "1.1.0";
    public const string DecoderUrl = "https://github.com/vgmstream/vgmstream/releases/download/r2117/vgmstream-win64.zip";
    public const string DecoderSha256 = "6c4a8a3813864fefed081bbd337dbc0ad93bf88e0b92f5db98d7ab258b22dc6c";
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=true,WriteIndented=true };
}
public sealed record ProgressUpdate(int Percent, string Message);
public sealed class MusicTrack
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string File { get; set; } = "";
    public bool Menu { get; set; }
    public bool Drive { get; set; }
    public bool Alternate { get; set; }
    public int SourceStream { get; set; }
    public double DurationSeconds { get; set; }
    public string Sha256 { get; set; } = "";
}
public sealed class MusicLibrary
{
    public string SourceArchive { get; set; } = "SDATA/sdat.viv";
    public string SourceArchiveSha256 { get; set; } = "";
    public string SourceExeSha256 { get; set; } = "";
    public string Decoder { get; set; } = "vgmstream r2117";
    public List<MusicTrack> Tracks { get; set; } = [];
}
public sealed record SourceInfo(string Root, string ArchivePath, string ExecutablePath, long AudioOffset, long AudioSize, byte[] Map, List<MusicTrack> Tracks);
public sealed record ExtractionResult(string Folder, MusicLibrary Library);
public sealed record GameInfo(string Root, string OptionsPath, string FmodVersion);

public static class OwnedWork
{
    public static string Create(string parent)
    {
        string path=Path.Combine(Path.GetFullPath(parent),"work-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path,".heatwarped-ug2-owned"),ModInfo.Id);
        return path;
    }
    public static void Delete(string path,string parent)
    {
        string full=Path.GetFullPath(path),expected=Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        if(!Directory.Exists(full))return;
        if(!string.Equals(Path.GetDirectoryName(full),expected,StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("work-",StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(full)[5..],"N",out _) ||
            (File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0 ||
            !File.Exists(Path.Combine(full,".heatwarped-ug2-owned")) ||
            File.ReadAllText(Path.Combine(full,".heatwarped-ug2-owned"))!=ModInfo.Id)
            throw new IOException("Refusing to delete a directory that is not an owned temporary work folder.");
        foreach(var d in Directory.EnumerateDirectories(full,"*",SearchOption.AllDirectories))
            if((File.GetAttributes(d)&FileAttributes.ReparsePoint)!=0)throw new IOException("A temporary directory contains a reparse point.");
        Directory.Delete(full,true);
    }
}
