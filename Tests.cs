using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CardboardPatcher;

static class Tests
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateHardLink(string link,string existing,IntPtr attributes);
    public static void Snapshot(string source,string output,string feed)
    {
        source=Path.GetFullPath(source);output=Path.GetFullPath(output);
        if(Directory.Exists(output))throw new InvalidOperationException("Snapshot test output must be a new disposable directory.");
        Directory.CreateDirectory(Path.Combine(output,"mods"));var originals=new Dictionary<string,string>();
        foreach(string jar in Directory.GetFiles(Path.Combine(source,"mods"),"*.jar"))
        {
            originals[jar]=Engine.Hash(jar);
            string dest=Path.Combine(output,"mods",Path.GetFileName(jar));
            if(!CreateHardLink(dest,jar,IntPtr.Zero))File.Copy(jar,dest); // Cross-volume snapshots require copies.
        }
        byte[] options=File.ReadAllBytes(Path.Combine(source,"options.txt"));File.WriteAllBytes(Path.Combine(output,"options.txt"),options);
        Directory.CreateDirectory(Path.Combine(output,"saves","Keep"));File.WriteAllText(Path.Combine(output,"saves","Keep","level.dat"),"test-world");
        var log=new List<string>();var engine=new Engine(local:true,progress:s=>log.Add(s),processGuard:_=>{});
        var release=engine.Fetch(feed).GetAwaiter().GetResult();
        if(new Uri(feed).IsFile)release=release with{Bundle=release.Bundle with{Url=new Uri(Path.Combine(Path.GetDirectoryName(new Uri(feed).LocalPath)!,"cardboard-assets.zip")).AbsoluteUri}};
        string backup=engine.Apply(output,release).GetAwaiter().GetResult();
        foreach(var f in release.Files)
            if(f.Mode!="default"&&!Engine.DefaultOnly(f.Path))
                if(Engine.Hash(Engine.Safe(output,f.Path))!=f.Sha512)throw new InvalidDataException("Installed file differs: "+f.Path);
        if(!File.ReadAllBytes(Path.Combine(output,"options.txt")).SequenceEqual(options))throw new Exception("Player options changed.");
        if(File.ReadAllText(Path.Combine(output,"saves","Keep","level.dat"))!="test-world")throw new Exception("World changed.");
        if(File.Exists(Path.Combine(output,"mods","minevolts-presentation-0.2.9.jar"))&&File.Exists(Path.Combine(output,"mods","minevolts-presentation-0.2.9-friends.1.jar")))throw new Exception("Duplicate presentation helpers.");
        foreach(var pair in originals)if(Engine.Hash(pair.Key)!=pair.Value)throw new Exception("Original source hardlink contents changed.");
        bool noOp=engine.Apply(output,release).GetAwaiter().GetResult()=="";
        engine.Rollback(output);
        foreach(var pair in originals)if(Engine.Hash(Path.Combine(output,"mods",Path.GetFileName(pair.Key)))!=pair.Value)throw new Exception("Snapshot rollback differs.");
        Json.Write(Path.Combine(output,"snapshot-results.json"),new{files=release.Files.Length,sourceMods=originals.Count,updateInstalled=true,allManagedHashesMatch=true,sourceFilesUnchanged=true,playerOptionsPreserved=true,worldPreserved=true,renamedHelperRetired=true,noOpPassed=noOp,rollbackRestoredAllSourceMods=true,gameLaunched=false,backup,log});
    }
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);var checks=new List<string>();
        string root=Path.Combine(output,"profile-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"mods"));
        void Write(string path,string text){string p=Path.Combine(root,path);Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.WriteAllText(p,text);}
        void Assert(bool ok,string label){if(!ok)throw new Exception("FAILED: "+label);checks.Add(label);}
        void Reject(Action action,string label){bool failed=false;try{action();}catch{failed=true;}Assert(failed,label);}
        Write("options.txt","player-controls");Write("saves/Keep/level.dat","world");Write("journeymap/keep.txt","map");Write("voxy/keep.dat","lod");Write("mods/a.jar","a1");Write("mods/b.jar","b1");Write("mods/c.jar","c1");Write("mods/personal.jar","personal");
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);string pub=key.ExportSubjectPublicKeyInfoPem();
        var engine=new Engine(pub,true,null,_=>{});
        string zip=Path.Combine(output,"fixture-assets.zip");
        using(var z=ZipFile.Open(zip,ZipArchiveMode.Create))
        {foreach(var pair in new[]{("mods/a.jar","a2"),("mods/d.jar","d1"),("options.txt","pack-controls")}){using var w=new StreamWriter(z.CreateEntry("payload/"+pair.Item1).Open(),new UTF8Encoding(false));w.Write(pair.Item2);}}
        PackFile F(string path,string text)=>new(path,Encoding.UTF8.GetByteCount(text),Engine.Hash(Encoding.UTF8.GetBytes(text)),null,"payload/"+path);
        var bundle=new Bundle(new Uri(zip).AbsoluteUri,new FileInfo(zip).Length,Engine.Hash(zip));
        var baseFiles=new[]{F("mods/a.jar","a1"),F("mods/b.jar","b1"),F("mods/c.jar","c1")};
        var r1=new Release(1,Engine.Product,"test.1",1,"1.21.1","21.1.240",bundle,baseFiles,[]);
        string Signed(Release release,string name)
        {
            byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(release,Json.Options);
            string path=Path.Combine(output,name);Json.Write(path,new Envelope(Convert.ToBase64String(bytes),Convert.ToBase64String(key.SignData(bytes,HashAlgorithmName.SHA256))));return new Uri(path).AbsoluteUri;
        }
        var fetched=engine.Fetch(Signed(r1,"valid.json")).GetAwaiter().GetResult();Assert(fetched.Version==r1.Version,"valid signed feed accepted");
        var bad=Json.Read<Envelope>(new Uri(Signed(r1,"bad.json")).LocalPath);byte[] altered=Convert.FromBase64String(bad.Manifest);altered[0]^=1;Json.Write(Path.Combine(output,"bad.json"),bad with{Manifest=Convert.ToBase64String(altered)});
        Reject(()=>engine.Fetch(new Uri(Path.Combine(output,"bad.json")).AbsoluteUri).GetAwaiter().GetResult(),"tampered signature rejected");
        Reject(()=>engine.Validate(r1 with{Files=[F("../outside.jar","x")]}),"path traversal rejected");
        Reject(()=>engine.Validate(r1 with{Files=[F("saves/Keep/level.dat","x")]}),"world payload rejected");
        Reject(()=>engine.Validate(r1 with{Files=[F("config/run.ps1","x")]}),"executable payload rejected");
        Reject(()=>engine.Validate(r1 with{Files=[F("mods/a.jar","x"),F("MODS/A.JAR","x")]}),"case-insensitive duplicate rejected");
        Reject(()=>engine.Validate(r1 with{Files=[F("mods/a.jar","x") with{Mode="existing"}]}),"existing-only file with download source rejected");
        Reject(()=>Engine.Safe(root,"mods/CON.jar"),"reserved Windows name rejected");
        Reject(()=>Engine.Safe(root,"mods/a.jar:stream"),"alternate data stream rejected");
        engine.Apply(root,r1).GetAwaiter().GetResult();Assert(Engine.ReadState(root)?.Version=="test.1","existing profile adopted");
        Assert(engine.Apply(root,r1).GetAwaiter().GetResult()=="","no-op produces no backup");
        var existing=r1 with{Files=baseFiles.Append(F("mods/base-only.jar","required") with{Mode="existing",Url=null,Entry=null}).ToArray()};
        Reject(()=>engine.Apply(root,existing).GetAwaiter().GetResult(),"missing base-only binary blocks update before writes");
        string store=Path.Combine(output,"shared-store.jar");File.WriteAllText(store,"a1");
        File.Delete(Path.Combine(root,"mods/a.jar"));Assert(CreateHardLink(Path.Combine(root,"mods/a.jar"),store,IntPtr.Zero),"hardlink fixture created");
        var r2=r1 with{Version="test.2",Sequence=2,Files=[F("mods/a.jar","a2"),F("mods/b.jar","b1"),F("mods/c.jar","c1"),F("mods/d.jar","d1"),F("options.txt","pack-controls") with{Mode="default"}]};
        using(var socket=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0))
        {
            socket.Start();int port=((System.Net.IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();
            using var listener=new System.Net.HttpListener();listener.Prefixes.Add($"http://127.0.0.1:{port}/");listener.Start();
            var serving=Task.Run(async()=>{var c=await listener.GetContextAsync();c.Response.StatusCode=503;c.Response.Close();});
            var offline=r2 with{Files=r2.Files.Select(f=>f.Path=="mods/a.jar"?f with{Url=$"http://127.0.0.1:{port}/unavailable.jar",Entry=null}:f).ToArray()};
            Reject(()=>engine.Apply(root,offline).GetAwaiter().GetResult(),"HTTP download failure aborts update before writes");serving.GetAwaiter().GetResult();
            Assert(File.ReadAllText(Path.Combine(root,"mods/a.jar"))=="a1"&&Engine.ReadState(root)?.Version=="test.1","HTTP failure preserves installed files and state");
        }
        engine.Apply(root,r2).GetAwaiter().GetResult();
        Assert(File.ReadAllText(Path.Combine(root,"mods/a.jar"))=="a2"&&File.Exists(Path.Combine(root,"mods/d.jar")),"replacement and addition installed");
        Assert(File.ReadAllText(store)=="a1","launcher shared hardlink contents preserved");
        Assert(File.ReadAllText(Path.Combine(root,"options.txt"))=="player-controls","player controls preserved");
        Assert(File.ReadAllText(Path.Combine(root,"saves/Keep/level.dat"))=="world"&&File.ReadAllText(Path.Combine(root,"journeymap/keep.txt"))=="map"&&File.ReadAllText(Path.Combine(root,"voxy/keep.dat"))=="lod","worlds maps and LOD caches preserved");
        Assert(File.Exists(Path.Combine(root,"mods/personal.jar")),"unknown player mod preserved");
        engine.Rollback(root);Assert(Engine.ReadState(root)?.Version=="test.1"&&File.ReadAllText(Path.Combine(root,"mods/a.jar"))=="a1"&&!File.Exists(Path.Combine(root,"mods/d.jar")),"manual rollback restores files and installed state");
        var failing=new Engine(pub,true,null,_=>{}){AfterWrite=_=>throw new IOException("Simulated interrupted write")};
        Reject(()=>failing.Apply(root,r2).GetAwaiter().GetResult(),"apply failure reported");
        Assert(Engine.ReadState(root)?.Version=="test.1"&&File.ReadAllText(Path.Combine(root,"mods/a.jar"))=="a1","apply failure rolls back automatically");
        var corrupt=r2 with{Files=r2.Files.Select(f=>f.Path=="mods/a.jar"?f with{Sha512=new string('0',128)}:f).ToArray()};
        Reject(()=>engine.Apply(root,corrupt).GetAwaiter().GetResult(),"corrupt staged payload rejected before writes");
        Assert(File.ReadAllText(Path.Combine(root,"mods/a.jar"))=="a1","failed preparation leaves profile unchanged");
        var r3=r1 with{Version="test.3",Sequence=3,Files=[F("mods/a.jar","a1"),F("mods/b.jar","b1")]};
        Write("mods/c.jar","locally modified");Reject(()=>engine.Apply(root,r3).GetAwaiter().GetResult(),"modified obsolete mod is not silently deleted");
        Write("mods/c.jar","c1");engine.Apply(root,r3).GetAwaiter().GetResult();Assert(!File.Exists(Path.Combine(root,"mods/c.jar")),"known obsolete managed mod removed");
        Reject(()=>engine.Apply(root,r1).GetAwaiter().GetResult(),"older release replay rejected");engine.Rollback(root);
        File.SetAttributes(Path.Combine(root,"mods/a.jar"),FileAttributes.ReadOnly);
        Reject(()=>engine.Apply(root,r2).GetAwaiter().GetResult(),"read-only launcher-managed file fails before patching");File.SetAttributes(Path.Combine(root,"mods/a.jar"),FileAttributes.Normal);
        var blocked=new Engine(pub,true,null,_=>throw new InvalidOperationException("Game running"));
        Reject(()=>blocked.Apply(root,r2).GetAwaiter().GetResult(),"running-app guard blocks update");
        Reject(()=>engine.Apply(root,r1 with{Files=[F("mods/a.jar","missing-match")]}).GetAwaiter().GetResult(),"bad archive source cannot change profile");
        // Durable journal recovery after a process disappeared mid-transaction.
        string backup=Engine.Safe(Engine.Work(root),"backups/99999999-interrupted");Directory.CreateDirectory(Engine.Safe(backup,"files/mods"));
        File.Copy(Engine.Safe(Engine.Work(root),"state.json"),Engine.Safe(backup,"previous-state.json"));
        File.Copy(Engine.Safe(root,"mods/a.jar"),Engine.Safe(backup,"files/mods/a.jar"));Write("mods/a.jar","half-applied");
        Json.Write(Engine.Safe(backup,"journal.json"),new Journal{HadState=true,Version="interrupted",Changes=[new("mods/a.jar",true)]});
        engine.Apply(root,r1).GetAwaiter().GetResult();Assert(File.ReadAllText(Engine.Safe(root,"mods/a.jar"))=="a1","next run recovers unfinished transaction journal");
        Assert(Json.Read<Journal>(Engine.Safe(backup,"journal.json")).Status=="recovered","recovery marked complete");
        Json.Write(Path.Combine(output,"test-results.json"),new{passed=checks.Count,checks,profile=root,realPlayerProfileTouched=false});
    }
}
