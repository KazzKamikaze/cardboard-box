using System.Text.Json;

namespace CardboardPatcher;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if(args.Length>0)
            {
                switch(args[0])
                {
                    case "--test": Tests.Run(args[1]);return 0;
                    case "--test-snapshot":Tests.Snapshot(args[1],args[2],args[3]);return 0;
                    case "--verify-release":
                    {
                        var e=new Engine(local:true);var r=e.Fetch(new Uri(Path.GetFullPath(args[1])).AbsoluteUri).GetAwaiter().GetResult();
                        string bundle=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!,"cardboard-assets.zip");
                        if(new FileInfo(bundle).Length!=r.Bundle.Size||Engine.Hash(bundle)!=r.Bundle.Sha512)throw new InvalidDataException("Published bundle checksum mismatch.");
                        using var z=System.IO.Compression.ZipFile.OpenRead(bundle);
                        foreach(var f in r.Files.Where(f=>f.Entry!=null))
                        {var entry=z.GetEntry(f.Entry!)??throw new InvalidDataException(f.Path);using var stream=entry.Open();if(entry.Length!=f.Size||Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(stream)).ToLowerInvariant()!=f.Sha512)throw new InvalidDataException(f.Path);}
                        Json.Write(args[2],new{r.Version,r.Sequence,files=r.Files.Length,mods=r.Files.Count(f=>f.Path.StartsWith("mods/")),signatureVerified=true,bundleHashesVerified=true,existingOnly=r.Files.Where(f=>f.Mode=="existing").Select(f=>f.Path).ToArray(),retired=r.Retired.Length});return 0;
                    }
                    case "--build-release":Publisher.Build(args[1],args[2],args[3],args[4],long.Parse(args[5]));return 0;
                    case "--check":
                    {
                        var r=new Engine().Fetch(args.Length>1?args[1]:Engine.DefaultFeed).GetAwaiter().GetResult();
                        Json.Write(args.Length>2?args[2]:Path.Combine(AppContext.BaseDirectory,"last-check.json"),new{r.Version,r.Sequence,files=r.Files.Length,signatureVerified=true});return 0;
                    }
                    case "--auto":
                    {
                        string root=Path.GetFullPath(args[1]);var e=new Engine(progress:s=>File.AppendAllText(Path.Combine(Path.GetTempPath(),"cardboard-patcher.log"),DateTime.Now+" "+s+Environment.NewLine));
                        var r=e.Fetch(args.Length>2?args[2]:Engine.DefaultFeed).GetAwaiter().GetResult();e.Apply(root,r).GetAwaiter().GetResult();return 0;
                    }
                    case "--preview":
                    {
                        ApplicationConfiguration.Initialize();using var f=new MainForm(false);
                        f.ShowInTaskbar=false;f.StartPosition=FormStartPosition.Manual;f.Location=new Point(-32000,-32000);f.Show();Application.DoEvents();
                        using var b=new Bitmap(f.Width,f.Height);f.DrawToBitmap(b,new Rectangle(0,0,b.Width,b.Height));b.Save(args[1]);f.Close();return 0;
                    }
                    default:throw new ArgumentException("Unknown patcher option.");
                }
            }
            ApplicationConfiguration.Initialize();Application.Run(new MainForm());return 0;
        }
        catch(Exception ex)
        {
            string log=Path.Combine(Path.GetTempPath(),"cardboard-patcher-error.txt");File.WriteAllText(log,ex.ToString());
            if(args.Length==0)MessageBox.Show(ex.Message,"Cardboard Box Patcher",MessageBoxButtons.OK,MessageBoxIcon.Error);
            else if(args.Length>0&&args[0]=="--auto")MessageBox.Show(ex.Message,"Cardboard Box update was not installed",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            return 1;
        }
    }
}
