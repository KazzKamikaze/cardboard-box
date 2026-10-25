using System.Diagnostics;

namespace CardboardPatcher;

sealed class MainForm:Form
{
    readonly bool preview;
    protected override bool ShowWithoutActivation=>preview;
    readonly TextBox folder=new(),history=new();readonly Label version=new(),status=new();
    readonly Button check=new(),update=new(),rollback=new();readonly CheckBox automatic=new();
    readonly System.Windows.Forms.Timer timer=new(){Interval=15*60*1000};
    readonly string settingsPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CardboardBoxPatcher","settings.json");
    Settings settings=new();Release? release;bool busy;
    static readonly Color Paper=Color.FromArgb(242,232,214),Ink=Color.FromArgb(50,44,37),Card=Color.FromArgb(213,180,132),Accent=Color.FromArgb(65,103,96);
    public MainForm(bool live=true)
    {
        preview=!live;
        Text="Cardboard Box Patcher";ClientSize=new Size(820,600);MinimumSize=new Size(836,639);MaximumSize=MinimumSize;MaximizeBox=false;
        BackColor=Paper;ForeColor=Ink;Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterScreen;
        var header=new Panel{Location=new(0,0),Size=new(820,106),BackColor=Card};Controls.Add(header);
        header.Paint+=(_,e)=>
        {
            using var pen=new Pen(Ink,2);e.Graphics.DrawRectangle(pen,30,25,50,48);e.Graphics.DrawLine(pen,30,38,80,38);e.Graphics.DrawLine(pen,55,25,55,73);
            using var tape=new SolidBrush(Color.FromArgb(237,214,168));e.Graphics.FillRectangle(tape,49,25,12,48);e.Graphics.DrawRectangle(pen,30,25,50,48);
        };
        header.Controls.Add(new Label{Text="CARDBOARD BOX",Font=new Font("Segoe UI Semibold",23,FontStyle.Bold),AutoSize=true,Location=new(102,18)});
        header.Controls.Add(new Label{Text="Pack updates, kept together.",AutoSize=true,Location=new(106,62)});
        Controls.Add(new Label{Text="MODRINTH GAME FOLDER",Font=new Font("Segoe UI Semibold",9,FontStyle.Bold),AutoSize=true,Location=new(30,126)});
        folder.SetBounds(30,151,638,31);folder.ReadOnly=true;folder.BackColor=Color.White;Controls.Add(folder);
        var browse=Button("Choose…",682,149,108,35);browse.Click+=(_,_)=>Choose();
        version.SetBounds(30,195,760,25);version.Text="Installed: select your Cardboard Box profile";Controls.Add(version);
        automatic.SetBounds(30,231,750,29);automatic.Text="Install approved updates automatically when Minecraft and Modrinth are closed";automatic.Checked=true;Controls.Add(automatic);
        automatic.CheckedChanged+=(_,_)=>{settings.AutoInstall=automatic.Checked;Save();};
        Configure(check,"Check for updates",30,278,180);check.Click+=async(_,_)=>await Run(false);
        Configure(update,"Install update",224,278,180);update.BackColor=Accent;update.ForeColor=Color.White;update.Click+=async(_,_)=>await Run(true);update.Enabled=false;
        Configure(rollback,"Restore backup",418,278,180);rollback.Click+=async(_,_)=>await Restore();
        var launcher=Button("Open Modrinth",612,278,178,42);launcher.Click+=(_,_)=>
        {if(busy)return;try{Process.Start(new ProcessStartInfo("modrinth://"){UseShellExecute=true});}catch{Log("Open Modrinth from your normal shortcut.");}};
        status.SetBounds(30,337,760,29);status.Font=new Font("Segoe UI Semibold",11);status.Text="Ready";Controls.Add(status);
        history.SetBounds(30,375,760,154);history.Multiline=true;history.ReadOnly=true;history.ScrollBars=ScrollBars.Vertical;history.BorderStyle=BorderStyle.FixedSingle;
        history.BackColor=Color.FromArgb(250,246,237);history.ForeColor=Ink;Controls.Add(history);
        Controls.Add(new Label{Text="Worlds, keybindings, maps and personal graphics settings stay yours.\nChecks on opening and every 15 minutes while this patcher is open.",Location=new(30,544),Size=new(760,46),Font=new Font("Segoe UI",9)});
        if(!live){folder.Text="…\\ModrinthApp\\profiles\\Cardboard Box";version.Text="Installed: 0.3.0-friends.3    •    Available: 0.3.0-friends.3";Log("Your pack is up to date. Backups are retained after each update.");return;}
        if(File.Exists(settingsPath)){try{settings=Json.Read<Settings>(settingsPath);}catch{Log("Saved patcher settings could not be read; choose your profile again.");}}
        folder.Text=settings.Profile;automatic.Checked=settings.AutoInstall;ShowVersion();
        if(string.IsNullOrEmpty(folder.Text))
        {
            string profiles=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"ModrinthApp","profiles");
            if(Directory.Exists(profiles))
            {
                var found=Directory.GetDirectories(profiles).Where(p=>File.Exists(Path.Combine(p,"config","fancymenu","customization","minevolts_main_menu.txt"))).ToArray();
                if(found.Length==1){folder.Text=found[0];settings.Profile=found[0];ShowVersion();}
            }
        }
        Shown+=async(_,_)=>await Run(settings.AutoInstall);
        timer.Tick+=async(_,_)=>await Run(automatic.Checked);timer.Start();
        FormClosing+=(_,e)=>{if(busy){e.Cancel=true;Log("Please wait for this operation to finish before closing.");}else Save();};
    }
    Button Button(string text,int x,int y,int width,int height){var b=new Button();Configure(b,text,x,y,width);b.Height=height;return b;}
    void Configure(Button b,string text,int x,int y,int width)
    {b.Text=text;b.SetBounds(x,y,width,42);b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderColor=Color.FromArgb(127,101,66);b.BackColor=Card;b.Cursor=Cursors.Hand;Controls.Add(b);}
    void Log(string text)
    {
        if(InvokeRequired){BeginInvoke(()=>Log(text));return;}
        status.Text=text.Length>100?text[..97]+"…":text;history.AppendText(DateTime.Now.ToString("HH:mm:ss")+"  "+text+Environment.NewLine);
    }
    void Save(){settings.Profile=folder.Text;settings.AutoInstall=automatic.Checked;Json.Write(settingsPath,settings);}
    void ShowVersion(){try{version.Text="Installed: "+(string.IsNullOrWhiteSpace(folder.Text)?"select a profile":Engine.ReadState(folder.Text)?.Version??"existing pack — not yet registered with patcher");}catch(Exception ex){Log(ex.Message);}}
    void Choose()
    {
        if(busy)return;using var dialog=new FolderBrowserDialog{Description="Choose the Cardboard Box game folder (contains mods and options.txt)",UseDescriptionForTitle=true,InitialDirectory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"ModrinthApp","profiles")};
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        folder.Text=dialog.SelectedPath;Save();ShowVersion();
    }
    void Busy(bool value){busy=value;check.Enabled=!value;update.Enabled=!value&&release!=null&&!string.IsNullOrWhiteSpace(folder.Text);rollback.Enabled=!value;}
    async Task Run(bool install)
    {
        if(busy)return;Busy(true);
        try
        {
            Log("Checking the signed Cardboard Box release…");
            var engine=new Engine(progress:Log);release=await engine.Fetch(settings.Feed);
            var state=string.IsNullOrWhiteSpace(folder.Text)?null:Engine.ReadState(folder.Text);
            version.Text=$"Installed: {state?.Version??"existing pack"}    •    Available: {release.Version}";
            if(!install){Log("Latest approved pack: "+release.Version+". Install also verifies and repairs managed files.");return;}
            if(string.IsNullOrWhiteSpace(folder.Text)){Log("Choose your imported Cardboard Box game folder to enable updates.");return;}
            Save();
            string root=folder.Text;string backup=await Task.Run(()=>engine.Apply(root,release));ShowVersion();
            Log(string.IsNullOrEmpty(backup)?"Your pack is up to date.":"Update complete. Open Modrinth to play; a rollback backup is available.");
        }
        catch(Exception ex){Log(ex.Message);}
        finally{Busy(false);}
    }
    async Task Restore()
    {
        if(busy||string.IsNullOrWhiteSpace(folder.Text))return;
        if(MessageBox.Show(this,"Restore the pack files from the last completed update? Personal settings and worlds are preserved.","Restore backup",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
        Busy(true);try{string root=folder.Text;await Task.Run(()=>new Engine(progress:Log).Rollback(root));ShowVersion();automatic.Checked=false;Log("Previous pack restored. Automatic installation is off until you enable it again.");}catch(Exception ex){Log(ex.Message);}finally{Busy(false);}
    }
}
