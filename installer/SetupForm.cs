using System.Diagnostics;
using System.Text.Json;
using HeatwarpedUG2;

namespace HeatwarpedUG2Setup;

public sealed class SetupForm : Form
{
    private readonly Label step=new(),description=new(),status=new(),summary=new();
    private readonly TextBox path=new();
    private readonly Button browse=new(),primary=new(),secondary=new(),cancel=new();
    private readonly ProgressBar progress=new();
    private readonly LinkLabel uninstall=new();
    private readonly Color background=Color.FromArgb(20,25,34),text=Color.FromArgb(230,235,242),accent=Color.FromArgb(96,220,193);
    private readonly string workParent=Path.Combine(Path.GetTempPath(),"HeatwarpedUG2Mod");
    private readonly string preferences=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HeatwarpedUG2Mod","last-paths.json");
    private string? work,nfsPath,gamePath;
    private ExtractionResult? extraction;
    private CancellationTokenSource? cancellation;
    private bool busy,closeWhenDone,removeMode,done;
    private int phase=1;
    public SetupForm(string? uninstallGame=null,bool remove=false)
    {
        Text="Heatwarped UG2 OST Mod — Setup";ClientSize=new Size(820,520);MinimumSize=new Size(760,540);StartPosition=FormStartPosition.CenterScreen;
        Font=new Font("Segoe UI",10);BackColor=background;ForeColor=text;
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(28),RowCount=9,ColumnCount=1};
        foreach(int height in new[]{49,43,78,48,47,43})layout.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,45));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,27));
        Controls.Add(layout);
        layout.Controls.Add(new Label{Text="HEATWARPED × UNDERGROUND 2",Font=new Font("Segoe UI",20,FontStyle.Bold),ForeColor=accent,AutoSize=true},0,0);
        step.AutoSize=true;step.Font=new Font(Font,FontStyle.Bold);layout.Controls.Add(step,0,1);
        description.Dock=DockStyle.Fill;layout.Controls.Add(description,0,2);
        var picker=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2};picker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));picker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,110));
        path.Dock=DockStyle.Fill;path.BackColor=Color.FromArgb(32,40,52);path.ForeColor=text;path.BorderStyle=BorderStyle.FixedSingle;
        browse.Text="Browse…";browse.Dock=DockStyle.Fill;Style(browse);browse.Click+=(_,_)=>Browse();picker.Controls.Add(path,0,0);picker.Controls.Add(browse,1,0);layout.Controls.Add(picker,0,3);
        progress.Dock=DockStyle.Top;progress.Height=18;layout.Controls.Add(progress,0,4);
        status.AutoSize=true;status.MaximumSize=new Size(750,0);status.ForeColor=accent;layout.Controls.Add(status,0,5);
        summary.Dock=DockStyle.Fill;summary.ForeColor=Color.FromArgb(164,181,201);layout.Controls.Add(summary,0,6);
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
        primary.AutoSize=true;primary.Height=34;Style(primary);primary.Click+=async(_,_)=>await RunPrimary();actions.Controls.Add(primary);
        secondary.Text="Open Music Settings";secondary.AutoSize=true;secondary.Height=34;secondary.Visible=false;Style(secondary);secondary.Click+=(_,_)=>OpenSettings();actions.Controls.Add(secondary);
        cancel.Text="Close";cancel.AutoSize=true;cancel.Height=34;Style(cancel);cancel.Click+=(_,_)=>{if(busy){closeWhenDone=true;cancellation?.Cancel();status.Text="Stopping safely…";}else Close();};actions.Controls.Add(cancel);layout.Controls.Add(actions,0,7);
        uninstall.Text="Uninstall an existing mod…";uninstall.AutoSize=true;uninstall.LinkColor=accent;uninstall.ActiveLinkColor=Color.White;uninstall.LinkClicked+=(_,_)=>{if(!busy){removeMode=true;phase=2;ConfigureStep();}};layout.Controls.Add(uninstall,0,8);
        try{if(File.Exists(preferences)){using var doc=JsonDocument.Parse(File.ReadAllText(preferences));nfsPath=doc.RootElement.GetProperty("nfs").GetString();gamePath=doc.RootElement.GetProperty("game").GetString();}}catch(Exception e)when(e is IOException or JsonException or KeyNotFoundException){}
        if(remove){removeMode=true;phase=2;gamePath=uninstallGame;}
        ConfigureStep();
        FormClosing+=(_,e)=>{if(busy){e.Cancel=true;closeWhenDone=true;cancellation?.Cancel();status.Text="Finishing the current operation safely…";}};
        FormClosed+=(_,_)=>Cleanup();
    }
    private void Style(Button b){b.FlatStyle=FlatStyle.Flat;b.BackColor=Color.FromArgb(38,48,62);b.ForeColor=text;b.FlatAppearance.BorderColor=Color.FromArgb(65,80,99);b.Margin=new Padding(0,0,10,0);}
    private void ConfigureStep()
    {
        progress.Value=0;status.Text="";
        if(removeMode)
        {
            step.Text="Uninstall the mod";description.Text="Choose your Heatwarped Demo installation. The loader will be disabled and the music will be kept in a disabled folder.";
            primary.Text="Uninstall mod";path.Text=gamePath??"";summary.Text="Close the game and Music Settings before continuing. Original game binaries and saves are not replaced.";uninstall.Visible=false;
        }
        else if(phase==1)
        {
            step.Text="1 / 2 — Select Need for Speed Underground 2";
            description.Text="Choose your installed PC copy of Underground 2. The installer will extract and convert its soundtrack locally.";
            path.Text=nfsPath??"";primary.Text="Extract soundtrack";
            summary.Text="Includes no music or game files. The first conversion downloads the official audio decoder (4.5 MB).\nAllow about 1.2 GB of temporary space. No Python or separate .NET installation is required.";
        }
        else
        {
            step.Text="2 / 2 — Select Heatwarped Demo";
            description.Text="Your 43 tracks are ready. Choose the folder containing Heatwarped.exe to install the audio loader and Music Settings.";
            path.Text=gamePath??"";primary.Text="Install mod";
            summary.Text="Defaults: 6 menu songs, 21 driving/racing songs, plus 16 selectable alternate versions.\nExisting mod settings are preserved. Allow about 1.2 GB free on the game drive. Close the game first.";
        }
    }
    private void Browse()
    {
        using var dialog=new FolderBrowserDialog{Description=phase==1&&!removeMode?"Select the NFS Underground 2 installation folder":"Select the Heatwarped Demo installation folder",UseDescriptionForTitle=true};
        if(Directory.Exists(path.Text))dialog.SelectedPath=path.Text;
        if(dialog.ShowDialog(this)==DialogResult.OK)path.Text=dialog.SelectedPath;
    }
    private void SetBusy(bool value)
    {
        busy=value;primary.Enabled=!value;browse.Enabled=!value;path.ReadOnly=value;uninstall.Enabled=!value;cancel.Text=value?"Cancel":"Close";
    }
    private async Task RunPrimary()
    {
        if(done){OpenFolder();return;}
        if(string.IsNullOrWhiteSpace(path.Text)){MessageBox.Show(this,"Choose an installation folder first.",Text);return;}
        SetBusy(true);cancellation=new CancellationTokenSource();
        var updates=new Progress<ProgressUpdate>(p=>{progress.Value=Math.Clamp(Math.Max(progress.Value,p.Percent),0,100);status.Text=p.Message;});
        try
        {
            if(removeMode)
            {
                string folder=path.Text;
                if(Program.RelocateUninstaller(folder)){closeWhenDone=true;return;}
                string disabled=await Task.Run(()=>ModInstaller.Uninstall(folder));gamePath=Path.GetFullPath(folder);
                done=true;description.Text="The mod has been disabled.";summary.Text="Music and settings were preserved in:\n"+disabled;primary.Text="Open game folder";status.Text="Uninstalled.";
            }
            else if(phase==1)
            {
                string folder=path.Text;status.Text="Checking the NFS installation…";
                SourceInfo source=await Task.Run(()=>SoundtrackExtractor.Inspect(folder),cancellation.Token);
                Cleanup();work=OwnedWork.Create(workParent);
                extraction=await new SoundtrackExtractor().ExtractAsync(source,work,updates,cancellation.Token);
                nfsPath=source.Root;phase=2;ConfigureStep();status.Text="43 tracks converted successfully. Select Heatwarped Demo below.";
            }
            else
            {
                if(extraction==null)throw new IOException("Convert the Underground 2 soundtrack first.");
                string folder=path.Text;status.Text="Checking Heatwarped Demo compatibility…";
                GameInfo game=await Task.Run(()=>ModInstaller.InspectGame(folder),cancellation.Token);
                var installer=new ModInstaller(Program.Loader(),Environment.ProcessPath!,Program.Notices());
                await installer.InstallAsync(game,extraction,updates,cancellation.Token);
                gamePath=game.Root;SavePreferences();done=true;step.Text="Ready to drive";description.Text="The Underground 2 soundtrack is now available in Heatwarped Demo.";
                summary.Text=$"Installed in: {gamePath}\n\nF8 — in-game Music Settings     F9 — next track\nSongs appear in the top-left corner for 4 seconds with a fade. Race starts and restarts advance the playlist. Your game audio sliders still apply.";
                primary.Text="Open game folder";secondary.Visible=true;uninstall.Visible=false;Cleanup();
            }
        }
        catch(OperationCanceledException){status.Text="Cancelled. Existing game files were left intact.";if(phase==1)Cleanup();}
        catch(Exception e)
        {
            status.Text="Operation stopped.";
            string message=e is UnauthorizedAccessException?"Windows denied write access. Close the application and run this installer as administrator for a protected game folder.":e.Message;
            MessageBox.Show(this,message,Text,MessageBoxButtons.OK,MessageBoxIcon.Error);
        }
        finally{SetBusy(false);cancellation.Dispose();cancellation=null;if(closeWhenDone)Close();}
    }
    private void SavePreferences()
    {
        try{Directory.CreateDirectory(Path.GetDirectoryName(preferences)!);File.WriteAllText(preferences,JsonSerializer.Serialize(new{nfs=nfsPath,game=gamePath}));}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    private void Cleanup()
    {
        if(work==null)return;try{OwnedWork.Delete(work,workParent);work=null;if(!done&&phase==1)extraction=null;}catch(IOException){}
    }
    private void OpenSettings(){if(gamePath!=null)Process.Start(new ProcessStartInfo(Path.Combine(gamePath,"CustomMusic","MusicSettings.exe")){UseShellExecute=true});}
    private void OpenFolder(){if(gamePath!=null)Process.Start(new ProcessStartInfo(gamePath){UseShellExecute=true});}
}
