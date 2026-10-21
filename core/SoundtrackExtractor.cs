using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HeatwarpedUG2;

public sealed class SoundtrackExtractor
{
    public static SourceInfo Inspect(string folder)
    {
        string root=Path.GetFullPath(folder.Trim().Trim('"'));
        string exe=Path.Combine(root,"SPEED2.EXE"),archive=Path.Combine(root,"SDATA","sdat.viv");
        if(!File.Exists(exe)||!File.Exists(archive))throw new InvalidDataException("Select the NFS Underground 2 installation folder containing SPEED2.EXE and SDATA\\sdat.viv.");
        using var input=File.OpenRead(archive);
        using var reader=new BinaryReader(input,Encoding.ASCII,true);
        if(input.Length<16||Encoding.ASCII.GetString(reader.ReadBytes(4))!="BIG4")throw new InvalidDataException("The NFS music archive is not a supported PC BIG4 archive.");
        uint size=ReadBig(reader),count=ReadBig(reader),directoryEnd=ReadBig(reader);
        if(size!=input.Length||count is 0 or >4096||directoryEnd<16||directoryEnd>input.Length)throw new InvalidDataException("The NFS archive header is damaged or unsupported.");
        (long Offset,long Size)? music=null,map=null;
        for(int i=0;i<count;i++)
        {
            long offset=ReadBig(reader),length=ReadBig(reader);
            var name=new StringBuilder();
            while(true)
            {
                if(input.Position>=directoryEnd||name.Length>512)throw new InvalidDataException("Invalid archive directory.");
                byte b=reader.ReadByte();if(b==0)break;name.Append((char)b);
            }
            if(offset<directoryEnd||length<0||offset>input.Length-length)throw new InvalidDataException("An archive entry exceeds the source file.");
            string normalized=name.ToString().Replace('\\','/').ToLowerInvariant();
            if(normalized=="pfdata/musicsfx.mus") {if(music!=null)throw new InvalidDataException("Duplicate music archive entry.");music=(offset,length);}
            if(normalized=="pfdata/musicsfx.mpf") {if(map!=null)throw new InvalidDataException("Duplicate music map entry.");map=(offset,length);}
        }
        if(music is null||map is null||map.Value.Size>8*1024*1024||music.Value.Size<1000000)throw new InvalidDataException("MusicSFx.mus / MusicSFx.mpf were not found in this archive.");
        byte[] mpf=new byte[checked((int)map.Value.Size)];input.Position=map.Value.Offset;input.ReadExactly(mpf);
        if(mpf.Length<0x20||!mpf.AsSpan(0,5).SequenceEqual("xDFP\x04"u8)||U16(mpf,0x12)<162)throw new InvalidDataException("Only the original PC Underground 2 Pathfinder v4 soundtrack is supported.");
        var pe=new NfsExecutable(exe);
        List<string[]> songs=pe.FindSongTable();
        var tracks=new Dictionary<int,MusicTrack>();
        int Sample(int node)
        {
            int at=checked(U16(mpf,checked(0x20+node*2))*4);
            int value=U16(mpf,at);
            if(value<1||value>43)throw new InvalidDataException("Unsupported music stream layout.");
            return value;
        }
        // The first 81 nodes describe 27 full songs; the next 81 contain alternate versions.
        // Register all primary songs first so a shared sample retains its full-track title.
        for(int alternate=0;alternate<2;alternate++)for(int i=0;i<27;i++)
        {
            int sample=Sample(alternate*81+i*3+2);
            if(tracks.ContainsKey(sample))continue;
            string suffix=alternate==1?" [alternate version]":"";
            string title=songs[i][0].Trim('"')+suffix,artist=songs[i][1];
            string filename=$"{sample:00} - {artist} - {title}";
            foreach(char c in Path.GetInvalidFileNameChars())filename=filename.Replace(c,'_');
            if(filename.Length>180)filename=filename[..180];
            tracks[sample]=new MusicTrack {Id=$"ug2_{sample:00}",SourceStream=sample,Title=title,Artist=artist,Album=songs[i][2],
                Alternate=alternate==1,Menu=alternate==0&&songs[i][3]=="FE",Drive=alternate==0&&songs[i][3]=="IG",File="Tracks/"+filename+".wav"};
        }
        if(tracks.Count!=43||tracks.Values.Count(t=>!t.Alternate)!=27)throw new InvalidDataException("This soundtrack layout is not supported. No game files have been changed.");
        return new SourceInfo(root,archive,exe,music.Value.Offset,music.Value.Size,mpf,tracks.OrderBy(p=>p.Key).Select(p=>p.Value).ToList());
    }
    public async Task<ExtractionResult> ExtractAsync(SourceInfo source,string work,IProgress<ProgressUpdate>? progress,CancellationToken token,string? decoderZip=null)
    {
        token.ThrowIfCancellationRequested();
        string decoder=await PrepareDecoderAsync(work,progress,token,decoderZip);
        string original=Path.Combine(work,"source"),converted=Path.Combine(work,"converted");
        Directory.CreateDirectory(original);Directory.CreateDirectory(Path.Combine(converted,"Tracks"));
        string mus=Path.Combine(original,"MusicSFx.mus"),mpf=Path.Combine(original,"MusicSFx.mpf");
        await File.WriteAllBytesAsync(mpf,source.Map,token);
        await using(var input=File.OpenRead(source.ArchivePath))
        await using(var output=File.Create(mus))
        {
            input.Position=source.AudioOffset;long remaining=source.AudioSize;byte[] buffer=new byte[1024*1024];
            while(remaining>0) {int n=await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,remaining)),token);if(n==0)throw new EndOfStreamException();await output.WriteAsync(buffer.AsMemory(0,n),token);remaining-=n;}
        }
        var library=new MusicLibrary {Tracks=source.Tracks};
        library.SourceArchiveSha256=await HashAsync(source.ArchivePath,token);
        library.SourceExeSha256=await HashAsync(source.ExecutablePath,token);
        int completed=0;
        await Parallel.ForEachAsync(library.Tracks,new ParallelOptions{MaxDegreeOfParallelism=4,CancellationToken=token},async(track,ct)=>
        {
            string output=Path.Combine(converted,track.File);
            var info=new ProcessStartInfo(decoder){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=original};
            foreach(string arg in new[]{"-i","-s",track.SourceStream.ToString(),"-o",output,mpf})info.ArgumentList.Add(arg);
            using var process=Process.Start(info)??throw new IOException("Unable to start the audio decoder.");
            using var registration=ct.Register(()=>{try {if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){} });
            var stdout=process.StandardOutput.ReadToEndAsync(ct);var stderr=process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            string message=await stdout+await stderr;
            if(process.ExitCode!=0)throw new InvalidDataException($"Unable to decode stream {track.SourceStream}: {message}");
            track.DurationSeconds=InspectWave(output);
            track.Sha256=await HashAsync(output,ct);
            int n=Interlocked.Increment(ref completed);
            progress?.Report(new ProgressUpdate(10+n*85/43,$"Converted {n}/43: {track.Artist} — {track.Title}"));
        });
        await File.WriteAllTextAsync(Path.Combine(converted,"library.json"),JsonSerializer.Serialize(library,ModInfo.Json),Encoding.UTF8,token);
        progress?.Report(new ProgressUpdate(100,"43 tracks ready: 27 full songs and 16 alternate versions."));
        return new ExtractionResult(converted,library);
    }
    private static async Task<string> PrepareDecoderAsync(string work,IProgress<ProgressUpdate>? progress,CancellationToken token,string? zipOverride)
    {
        string cache=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HeatwarpedUG2Mod","Downloads");
        Directory.CreateDirectory(cache);
        string zip=zipOverride??Path.Combine(cache,"vgmstream-r2117-win64.zip");
        if(!File.Exists(zip)||!string.Equals(await HashAsync(zip,token),ModInfo.DecoderSha256,StringComparison.OrdinalIgnoreCase))
        {
            if(zipOverride!=null)throw new InvalidDataException("The decoder archive failed its SHA-256 verification.");
            progress?.Report(new ProgressUpdate(1,"Downloading the official vgmstream r2117 decoder (4.5 MB)…"));
            string download=Path.Combine(work,"decoder.download");
            using var client=new HttpClient{Timeout=TimeSpan.FromMinutes(3)};
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HeatwarpedUG2Mod/1.1");
            using var response=await client.GetAsync(ModInfo.DecoderUrl,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
            await using(var output=File.Create(download))await response.Content.CopyToAsync(output,token);
            if(!string.Equals(await HashAsync(download,token),ModInfo.DecoderSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The downloaded decoder failed SHA-256 verification. Installation was stopped.");
            File.Copy(download,zip,true);
        }
        progress?.Report(new ProgressUpdate(5,"Preparing the verified audio decoder…"));
        string tools=Path.Combine(work,"decoder");Directory.CreateDirectory(tools);
        using var archive=ZipFile.OpenRead(zip);
        foreach(var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string destination=Path.GetFullPath(Path.Combine(tools,entry.FullName));
            if(!destination.StartsWith(tools+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unsafe decoder archive entry.");
            if(entry.Name.Length==0){Directory.CreateDirectory(destination);continue;}
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);entry.ExtractToFile(destination,true);
        }
        string cli=Path.Combine(tools,"vgmstream-cli.exe");if(!File.Exists(cli))throw new InvalidDataException("The decoder executable is missing.");
        return cli;
    }
    public static async Task<string> HashAsync(string path,CancellationToken token=default)
    {
        await using var input=File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input,token)).ToLowerInvariant();
    }
    public static double InspectWave(string path)
    {
        using var reader=new BinaryReader(File.OpenRead(path));
        if(reader.BaseStream.Length<44||Encoding.ASCII.GetString(reader.ReadBytes(4))!="RIFF")throw new InvalidDataException("Decoder output is not a WAV file.");
        reader.ReadUInt32();if(Encoding.ASCII.GetString(reader.ReadBytes(4))!="WAVE")throw new InvalidDataException("Invalid WAV header.");
        uint bytesPerSecond=0;long dataSize=0;
        while(reader.BaseStream.Position+8<=reader.BaseStream.Length)
        {
            string type=Encoding.ASCII.GetString(reader.ReadBytes(4));uint size=reader.ReadUInt32();long end=reader.BaseStream.Position+size;
            if(end>reader.BaseStream.Length)throw new InvalidDataException("Truncated WAV output.");
            if(type=="fmt ")
            {
                if(size<16)throw new InvalidDataException("Invalid WAV format.");
                ushort format=reader.ReadUInt16(),channels=reader.ReadUInt16();uint rate=reader.ReadUInt32();bytesPerSecond=reader.ReadUInt32();reader.ReadUInt16();ushort bits=reader.ReadUInt16();
                if(format!=1||channels!=2||rate!=32000||bits!=16||bytesPerSecond!=128000)throw new InvalidDataException("Unexpected WAV format; expected 32 kHz stereo PCM16.");
            }
            if(type=="data")dataSize=size;
            reader.BaseStream.Position=end+(size&1);
        }
        if(bytesPerSecond==0||dataSize<128000)throw new InvalidDataException("The decoded track contains no usable music.");
        return dataSize/(double)bytesPerSecond;
    }
    private static uint ReadBig(BinaryReader reader)=>BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));
    private static ushort U16(byte[] data,int at)
    {
        if(at<0||at>data.Length-2)throw new InvalidDataException("The music map is truncated.");
        return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at,2));
    }
    private sealed class NfsExecutable
    {
        private readonly byte[] bytes;
        private readonly uint imageBase;
        private readonly List<(uint Rva,uint Raw,uint Size)> sections=[];
        public NfsExecutable(string path)
        {
            if(new FileInfo(path).Length>128*1024*1024)throw new InvalidDataException("Unsupported NFS executable size.");
            bytes=File.ReadAllBytes(path);
            if(bytes.Length<128||bytes[0]!='M'||bytes[1]!='Z')throw new InvalidDataException("SPEED2.EXE is not a valid Windows executable.");
            int pe=checked((int)U32(0x3c));
            if(U32(pe)!=0x4550||Read16(pe+24)!=0x10b)throw new InvalidDataException("A PC 32-bit NFS Underground 2 executable is required.");
            imageBase=U32(pe+24+28);int count=Read16(pe+6),table=pe+24+Read16(pe+20);
            if(count is <1 or >96)throw new InvalidDataException("Invalid PE section count.");
            for(int i=0;i<count;i++){int p=checked(table+i*40);sections.Add((U32(p+12),U32(p+20),U32(p+16)));}
        }
        public List<string[]> FindSongTable()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            byte[] prefix=Encoding.ASCII.GetBytes("\"Riders on the Storm");
            for(int start=0;start<bytes.Length-prefix.Length;)
            {
                int hit=bytes.AsSpan(start).IndexOf(prefix);if(hit<0)break;hit+=start;start=hit+1;
                uint address=ToAddress(hit);if(address==0)continue;
                byte[] pointer=BitConverter.GetBytes(address);
                for(int search=0;search<bytes.Length-27*16;)
                {
                    int table=bytes.AsSpan(search).IndexOf(pointer);if(table<0)break;table+=search;search=table+1;
                    try
                    {
                        var songs=new List<string[]>();
                        for(int i=0;i<27;i++)
                        {
                            string[] row=Enumerable.Range(0,4).Select(c=>ReadString(U32(table+i*16+c*4))).ToArray();
                            if(row[3]!=(i<6?"FE":"IG")||row[0].Length<3||row[1].Length==0)throw new InvalidDataException();
                            songs.Add(row);
                        }
                        return songs;
                    }
                    catch(Exception e) when(e is InvalidDataException or OverflowException or ArgumentException){}
                }
            }
            throw new InvalidDataException("The song table was not found in SPEED2.EXE. This executable or soundtrack version is not supported.");
        }
        private uint ToAddress(int offset)
        {
            foreach(var section in sections)if(offset>=section.Raw&&(long)offset<section.Raw+section.Size)return checked(imageBase+section.Rva+(uint)offset-section.Raw);
            return 0;
        }
        private string ReadString(uint address)
        {
            if(address<imageBase)throw new InvalidDataException();uint rva=address-imageBase;
            foreach(var section in sections)if(rva>=section.Rva&&rva-section.Rva<section.Size)
            {
                int p=checked((int)(section.Raw+rva-section.Rva)),end=p;
                while(end<bytes.Length&&end-p<512&&bytes[end]!=0)end++;
                if(end==bytes.Length||end-p==512)throw new InvalidDataException();
                return Encoding.GetEncoding(1252).GetString(bytes,p,end-p);
            }
            throw new InvalidDataException();
        }
        private uint U32(int at){if(at<0||at>bytes.Length-4)throw new InvalidDataException("Truncated NFS executable.");return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at,4));}
        private ushort Read16(int at){if(at<0||at>bytes.Length-2)throw new InvalidDataException();return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at,2));}
    }
}
