using System.ComponentModel;
using System.Globalization;
using System.Media;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HeatwarpedMusic;

public sealed class TrackRow
{
    public string File { get; set; } = "";
    public string Name { get; set; } = "";
    public string Duration { get; set; } = "";
    public bool Menu { get; set; }
    public bool Drive { get; set; }
    public bool DefaultMenu { get; set; }
    public bool DefaultDrive { get; set; }
}

internal static class Program
{
    public const string Title = "Heatwarped - Music Settings";
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int command);
    [STAThread]
    static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (args.Length>1 && args[0]=="--check") root = Path.GetFullPath(args[1]);
        if (args.Length>0 && args[0]=="--check")
        {
            try
            {
                var settings = new SettingsStore(root);
                settings.Load();
                if (settings.Tracks.Count==0 || settings.Tracks.Any(t=>!System.IO.File.Exists(Path.Combine(root,t.File)))) return 2;
                var report = new { count=settings.Tracks.Count, menu=settings.Tracks.Count(t=>t.Menu), drive=settings.Tracks.Count(t=>t.Drive), enabled=settings.Enabled };
                System.IO.File.WriteAllText(Path.Combine(root,"settings-check.json"),JsonSerializer.Serialize(report));
                return 0;
            }
            catch { return 3; }
        }
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16];
        using var mutex = new Mutex(true,"Local\\HeatwarpedMusicSettings_"+key,out bool fresh);
        if(!fresh) { var h=FindWindow(null,Title);ShowWindow(h,9);SetForegroundWindow(h);return 0; }
        try { Application.Run(new SettingsForm(root));return 0; }
        catch(Exception e) { MessageBox.Show(e.Message,"Unable to open Music Settings",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1; }
    }
}

public sealed class SettingsStore(string root)
{
    public string Root { get; } = root;
    public List<TrackRow> Tracks { get; } = [];
    public bool Enabled { get; set; } = true;
    public bool Shuffle { get; set; } = true;
    public double Gain { get; set; } = .8;
    public void Load()
    {
        using var doc=JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(Root,"library.json")));
        foreach(var t in doc.RootElement.GetProperty("tracks").EnumerateArray())
        {
            double seconds=t.GetProperty("durationSeconds").GetDouble();
            Tracks.Add(new TrackRow { File=t.GetProperty("file").GetString()!.Replace('/',Path.DirectorySeparatorChar),
                Name=t.GetProperty("artist").GetString()+" - "+t.GetProperty("title").GetString()!.Replace("[wersja alternatywna]", "[alternate version]"),
                Duration=$"{(int)seconds/60}:{(int)seconds%60:00}",
                Menu=t.GetProperty("menu").GetBoolean(),Drive=t.GetProperty("drive").GetBoolean(),
                DefaultMenu=t.GetProperty("menu").GetBoolean(),DefaultDrive=t.GetProperty("drive").GetBoolean() });
        }
        string path=Path.Combine(Root,"playlists.ini");
        if(!System.IO.File.Exists(path)) return;
        var sections=new Dictionary<string,Dictionary<string,string>>(StringComparer.OrdinalIgnoreCase);
        string section="";
        foreach(string raw in System.IO.File.ReadAllLines(path,Encoding.Unicode))
        {
            string line=raw.Trim();
            if(line.StartsWith('[')&&line.EndsWith(']')) {section=line[1..^1];sections[section]=new(StringComparer.OrdinalIgnoreCase);continue;}
            int split=line.IndexOf('=');
            if(split>0 && sections.TryGetValue(section,out var values))values[line[..split]]=line[(split+1)..];
        }
        if(sections.TryGetValue("General",out var general))
        {
            Enabled=general.GetValueOrDefault("Enabled","1")=="1";
            Shuffle=general.GetValueOrDefault("Shuffle","1")=="1";
            if(double.TryParse(general.GetValueOrDefault("Gain","0.8"),CultureInfo.InvariantCulture,out double gain)) Gain=Math.Clamp(gain,0,1);
        }
        foreach(var values in sections.Where(p=>p.Key.StartsWith("Track",StringComparison.OrdinalIgnoreCase)).Select(p=>p.Value))
        {
            string file=values.GetValueOrDefault("File","").Replace('/',Path.DirectorySeparatorChar);
            if(file.Length==0)continue;
            var t=Tracks.Find(x=>string.Equals(x.File,file,StringComparison.OrdinalIgnoreCase));
            if(t==null) {t=new TrackRow {File=file,Name=values.GetValueOrDefault("Title",Path.GetFileNameWithoutExtension(file)),DefaultMenu=true,DefaultDrive=true};Tracks.Add(t);}
            t.Menu=values.GetValueOrDefault("Menu","0")=="1";
            t.Drive=values.GetValueOrDefault("Drive","0")=="1";
        }
    }
    public void Save()
    {
        var b=new StringBuilder();
        b.AppendLine("[General]").AppendLine($"Enabled={(Enabled?1:0)}").AppendLine($"Shuffle={(Shuffle?1:0)}")
            .AppendLine("Gain="+Gain.ToString("0.00",CultureInfo.InvariantCulture)).AppendLine($"Count={Tracks.Count}");
        for(int i=0;i<Tracks.Count;i++)
        {
            var t=Tracks[i];
            b.AppendLine().AppendLine($"[Track{i}]").AppendLine("File="+Clean(t.File)).AppendLine("Title="+Clean(t.Name))
                .AppendLine($"Menu={(t.Menu?1:0)}").AppendLine($"Drive={(t.Drive?1:0)}");
        }
        string path=Path.Combine(Root,"playlists.ini"),temp=path+".tmp";
        System.IO.File.WriteAllText(temp,b.ToString(),Encoding.Unicode);
        System.IO.File.Move(temp,path,true);
    }
    private static string Clean(string s)=>s.Replace('\r',' ').Replace('\n',' ');
}

public sealed class SettingsForm : Form
{
    private readonly SettingsStore store;
    private readonly DataGridView grid=new();
    private readonly BindingList<TrackRow> rows;
    private readonly Label counts=new(),status=new(),saved=new();
    private readonly CheckBox enabled=new(){Text="Enable custom music",AutoSize=true},shuffle=new(){Text="Shuffle without repeats",AutoSize=true};
    private readonly TrackBar volume=new(){Minimum=0,Maximum=100,TickFrequency=10,Width=160};
    private readonly System.Windows.Forms.Timer timer=new(){Interval=1000};
    private SoundPlayer? preview;
    private bool ready;
    private readonly Color background=Color.FromArgb(20,25,34),foreground=Color.FromArgb(230,235,242),accent=Color.FromArgb(96,220,193);
    public SettingsForm(string root)
    {
        store=new SettingsStore(root);store.Load();rows=new BindingList<TrackRow>(store.Tracks);
        Text=Program.Title;ClientSize=new Size(1130,740);MinimumSize=new Size(900,600);StartPosition=FormStartPosition.CenterScreen;
        Font=new Font("Segoe UI",10);BackColor=background;ForeColor=foreground;
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(22),ColumnCount=1,RowCount=7};
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,50));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,52));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,35));
        Controls.Add(layout);
        layout.Controls.Add(new Label{Text="NFS UNDERGROUND 2  /  YOUR SOUNDTRACK",Font=new Font("Segoe UI",17,FontStyle.Bold),ForeColor=accent,AutoSize=true},0,0);
        layout.Controls.Add(new Label{Text="Choose where each track plays. Select both boxes to use both playlists. Changes are saved automatically.",AutoSize=true},0,1);
        grid.Dock=DockStyle.Fill;grid.AutoGenerateColumns=false;grid.AllowUserToAddRows=false;grid.AllowUserToDeleteRows=false;
        grid.RowHeadersVisible=false;grid.BackgroundColor=Color.FromArgb(27,33,44);grid.BorderStyle=BorderStyle.None;
        grid.EnableHeadersVisualStyles=false;grid.ColumnHeadersDefaultCellStyle=new DataGridViewCellStyle{BackColor=Color.FromArgb(40,49,62),ForeColor=foreground,Font=new Font(Font,FontStyle.Bold)};
        grid.DefaultCellStyle=new DataGridViewCellStyle{BackColor=Color.FromArgb(27,33,44),ForeColor=foreground,SelectionBackColor=Color.FromArgb(45,90,94),SelectionForeColor=Color.White};
        grid.AlternatingRowsDefaultCellStyle=new DataGridViewCellStyle{BackColor=Color.FromArgb(31,38,50)};
        grid.GridColor=Color.FromArgb(47,56,68);grid.RowTemplate.Height=30;grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect;grid.MultiSelect=false;
        grid.Columns.Add(new DataGridViewTextBoxColumn{HeaderText="Artist / track",DataPropertyName="Name",AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,ReadOnly=true,SortMode=DataGridViewColumnSortMode.NotSortable});
        grid.Columns.Add(new DataGridViewTextBoxColumn{HeaderText="Time",DataPropertyName="Duration",Width=65,ReadOnly=true,SortMode=DataGridViewColumnSortMode.NotSortable});
        grid.Columns.Add(new DataGridViewCheckBoxColumn{HeaderText="Menu",DataPropertyName="Menu",Width=85});
        grid.Columns.Add(new DataGridViewCheckBoxColumn{HeaderText="Driving / racing",DataPropertyName="Drive",Width=145});
        grid.DataSource=rows;layout.Controls.Add(grid,0,2);
        grid.CurrentCellDirtyStateChanged+=(_,_)=>{if(grid.IsCurrentCellDirty)grid.CommitEdit(DataGridViewDataErrorContexts.Commit);};
        grid.CellValueChanged+=(_,_)=>Save();
        var bulk=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
        AddButton(bulk,"All: menu",()=>SetAll(true,null));AddButton(bulk,"All: driving",()=>SetAll(null,true));
        AddButton(bulk,"Clear menu",()=>SetAll(false,null));AddButton(bulk,"Clear driving",()=>SetAll(null,false));
        AddButton(bulk,"UG2 defaults",()=>{foreach(var t in rows){t.Menu=t.DefaultMenu;t.Drive=t.DefaultDrive;}grid.Refresh();Save();});
        layout.Controls.Add(bulk,0,3);
        var controls=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Padding=new Padding(0,7,0,0)};
        enabled.Checked=store.Enabled;shuffle.Checked=store.Shuffle;volume.Value=(int)(store.Gain*100);
        controls.Controls.Add(enabled);controls.Controls.Add(shuffle);
        controls.Controls.Add(new Label{Text="Mod volume",AutoSize=true,Padding=new Padding(12,0,0,0)});controls.Controls.Add(volume);
        counts.AutoSize=true;counts.ForeColor=accent;controls.Controls.Add(counts);layout.Controls.Add(controls,0,4);
        enabled.CheckedChanged+=(_,_)=>Save();shuffle.CheckedChanged+=(_,_)=>Save();volume.ValueChanged+=(_,_)=>Save();
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
        AddButton(actions,"Add tracks…",AddFiles);AddButton(actions,"Preview selected",Preview);
        AddButton(actions,"Stop preview",StopPreview);AddButton(actions,"Open music folder",()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(root,"Tracks")){UseShellExecute=true}));
        saved.AutoSize=true;saved.Padding=new Padding(12,8,0,0);actions.Controls.Add(saved);layout.Controls.Add(actions,0,5);
        status.AutoSize=true;status.ForeColor=Color.FromArgb(158,175,195);layout.Controls.Add(status,0,6);
        timer.Tick+=(_,_)=>UpdateStatus();timer.Start();FormClosed+=(_,_)=>{timer.Stop();StopPreview();};
        grid.CellDoubleClick+=(_,e)=>{if(e.RowIndex>=0 && e.ColumnIndex==0)Preview();};
        ready=true;UpdateCounts();UpdateStatus();
    }
    private void AddButton(Control owner,string text,Action action)
    {
        var b=new Button{Text=text,AutoSize=true,Height=33,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(38,48,62),ForeColor=foreground,Margin=new Padding(0,0,8,0)};
        b.FlatAppearance.BorderColor=Color.FromArgb(65,80,99);b.Click+=(_,_)=>action();owner.Controls.Add(b);
    }
    private void Save()
    {
        if(!ready)return;
        try {grid.EndEdit();store.Enabled=enabled.Checked;store.Shuffle=shuffle.Checked;store.Gain=volume.Value/100d;store.Save();saved.Text="Saved ✓";saved.ForeColor=accent;UpdateCounts();}
        catch(Exception e){saved.Text="Save failed";saved.ForeColor=Color.Salmon;MessageBox.Show(e.Message,"Unable to save settings");}
    }
    private void UpdateCounts()=>counts.Text=$"Menu: {rows.Count(t=>t.Menu)}   Driving: {rows.Count(t=>t.Drive)}";
    private void SetAll(bool? menu,bool? drive){foreach(var t in rows){if(menu.HasValue)t.Menu=menu.Value;if(drive.HasValue)t.Drive=drive.Value;}grid.Refresh();Save();}
    private void AddFiles()
    {
        using var dialog=new OpenFileDialog{Title="Add your own music",Filter="Audio files|*.wav;*.mp3;*.ogg;*.flac;*.aif;*.aiff",Multiselect=true};
        if(dialog.ShowDialog()!=DialogResult.OK)return;
        foreach(string source in dialog.FileNames)
        {
            string filename=Path.GetFileName(source),dest=Path.Combine(store.Root,"Tracks",filename);
            if(!string.Equals(Path.GetFullPath(source),Path.GetFullPath(dest),StringComparison.OrdinalIgnoreCase))
            {
                if(System.IO.File.Exists(dest))dest=Path.Combine(store.Root,"Tracks",Path.GetFileNameWithoutExtension(filename)+"_"+Guid.NewGuid().ToString("N")[..6]+Path.GetExtension(filename));
                System.IO.File.Copy(source,dest);
            }
            string relative=Path.GetRelativePath(store.Root,dest);
            if(rows.Any(t=>string.Equals(t.File,relative,StringComparison.OrdinalIgnoreCase)))continue;
            rows.Add(new TrackRow{File=relative,Name=Path.GetFileNameWithoutExtension(source),Menu=true,Drive=true,DefaultMenu=true,DefaultDrive=true});
        }
        Save();
    }
    private void Preview()
    {
        StopPreview();
        if(grid.CurrentRow?.DataBoundItem is not TrackRow t)return;
        string path=Path.Combine(store.Root,t.File);
        try
        {
            if(Path.GetExtension(path).Equals(".wav",StringComparison.OrdinalIgnoreCase)){preview=new SoundPlayer(path);preview.Play();}
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path){UseShellExecute=true});
        }
        catch(Exception e){MessageBox.Show(e.Message,"Unable to play track");}
    }
    private void StopPreview(){preview?.Stop();preview?.Dispose();preview=null;}
    private void UpdateStatus()
    {
        try
        {
            var lines=System.IO.File.ReadAllLines(Path.Combine(store.Root,"now-playing.txt"));
            status.Text=lines.Length>=3 && lines[0]=="Playing" ? $"Now playing: {lines[1].Replace("Jazda / wyscigi", "Driving / racing")} • {lines[2].Replace("[wersja alternatywna]", "[alternate version]")}" : "In game: F8 — music settings, F9 — next track. Menu and driving volume also follow the game audio options.";
        }
        catch(IOException){status.Text="In game: F8 — music settings, F9 — next track. Settings are applied while the game is running.";}
    }
}
