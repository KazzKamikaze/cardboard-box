using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CardboardPatcher;

public sealed class Engine
{
    public const string DefaultFeed="https://github.com/KazzKamikaze/cardboard-box/releases/latest/download/cardboard-release.json";
    public const string Product="Cardboard Box";
    public static readonly HttpClient Http=CreateHttp();
    readonly string publicKey;
    readonly bool allowLocal;
    readonly Action<string>? log;
    readonly Action<string> guard;
    // Fault injection is used only by the isolated transaction tests.
    internal Action<int>? AfterWrite;
    public Engine(string? key=null,bool local=false,Action<string>? progress=null,Action<string>? processGuard=null)
    {publicKey=key??Trust.PublicKey;allowLocal=local;log=progress;guard=processGuard??EnsureAppsClosed;}
    static HttpClient CreateHttp()
    {
        var h=new HttpClient(new HttpClientHandler { AllowAutoRedirect=true }) {Timeout=TimeSpan.FromMinutes(10)};
        h.DefaultRequestHeaders.UserAgent.ParseAdd("CardboardBoxPatcher/1.0.1");return h;
    }
    public static string Hash(string path) {using var f=File.OpenRead(path);return Convert.ToHexString(SHA512.HashData(f)).ToLowerInvariant();}
    public static string Hash(byte[] data)=>Convert.ToHexString(SHA512.HashData(data)).ToLowerInvariant();
    public static string Work(string root)=>Safe(root,".cardboard-patcher");
    public static string Safe(string root,string relative)
    {
        if(string.IsNullOrWhiteSpace(relative)||relative.Contains('\\')||relative.Contains(':')||relative.StartsWith('/')||relative.Length>230)
            throw new InvalidDataException("Unsafe relative path: "+relative);
        var parts=relative.Split('/');
        foreach(string p in parts)
            if(p.Length==0||p is "." or ".."||p.EndsWith('.')||p.EndsWith(' ')||p.IndexOfAny(Path.GetInvalidFileNameChars())>=0||
                Regex.IsMatch(p,@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)",RegexOptions.IgnoreCase))
                throw new InvalidDataException("Unsafe path component: "+relative);
        string fullRoot=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string dest=Path.GetFullPath(Path.Combine(fullRoot,Path.Combine(parts)));
        if(!dest.StartsWith(fullRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Path leaves selected profile.");
        // Never follow profile junctions/symlinks into a different instance or drive.
        string current=Path.GetPathRoot(dest)!;
        foreach(string segment in dest[current.Length..].Split(Path.DirectorySeparatorChar))
        {
            current=Path.Combine(current,segment);
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked folders/files are not supported: "+current);
        }
        return dest;
    }
    public static bool DefaultOnly(string path)
    {
        string p=path.ToLowerInvariant();
        return p is "options.txt" or "servers.dat" || p.StartsWith("shaderpacks/")&&p.EndsWith(".txt") ||
            new[]{"config/iris","config/sodium","config/embeddium","config/voxy","config/mousetweaks","config/modernfix","config/entityculling","config/dynamic_fps","config/voicechat","config/xaero","config/journeymap"}.Any(p.StartsWith);
    }
    public static void ValidatePackPath(string path)
    {
        _=Safe(Path.GetTempPath(),path);
        var first=path.Split('/')[0].ToLowerInvariant();
        if(!new[]{"mods","config","defaultconfigs","kubejs","resourcepacks","shaderpacks","licenses","sources"}.Contains(first)&&
            !new[]{"options.txt","servers.dat","cardboard-box-readme.txt","cardboard-box-components.json"}.Contains(path.ToLowerInvariant()))
            throw new InvalidDataException("Release attempts to manage a protected location: "+path);
        if(Regex.IsMatch(path,@"\.(exe|dll|bat|cmd|ps1|lnk|msi|com|scr)$",RegexOptions.IgnoreCase))throw new InvalidDataException("Executable payload rejected: "+path);
    }
    void CheckUri(Uri uri)
    {
        if(uri.Scheme=="https")return;
        if(allowLocal&&(uri.IsFile||uri.IsLoopback&&uri.Scheme=="http"))return;
        throw new InvalidDataException("Update downloads must use HTTPS.");
    }
    public async Task<Release> Fetch(string feed,CancellationToken ct=default)
    {
        var uri=new Uri(feed);CheckUri(uri);
        byte[] bytes;
        if(uri.IsFile)bytes=await File.ReadAllBytesAsync(uri.LocalPath,ct);
        else
        {
            using var response=await Http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
            if(response.RequestMessage?.RequestUri is {} final)CheckUri(final);
            if(response.Content.Headers.ContentLength>8*1024*1024)throw new InvalidDataException("Release metadata is too large.");
            await using var stream=await response.Content.ReadAsStreamAsync(ct);using var output=new MemoryStream();
            await CopyLimited(stream,output,8*1024*1024,ct);bytes=output.ToArray();
        }
        if(bytes.Length>8*1024*1024)throw new InvalidDataException("Release metadata is too large.");
        var envelope=JsonSerializer.Deserialize<Envelope>(bytes,Json.Options)??throw new InvalidDataException("Missing release envelope.");
        var data=Convert.FromBase64String(envelope.Manifest);
        using var key=ECDsa.Create();key.ImportFromPem(publicKey);
        if(!key.VerifyData(data,Convert.FromBase64String(envelope.Signature),HashAlgorithmName.SHA256))throw new CryptographicException("Release signature is invalid. No files were changed.");
        var release=JsonSerializer.Deserialize<Release>(data,Json.Options)??throw new InvalidDataException("Missing release.");
        Validate(release);return release;
    }
    public void Validate(Release r)
    {
        if(r.Schema!=1||r.Product!=Product||r.Sequence<1||string.IsNullOrWhiteSpace(r.Version)||r.Version.Length>80)throw new InvalidDataException("Unsupported release identity.");
        if(new Version(r.MinimumPatcher)>typeof(Engine).Assembly.GetName().Version)throw new InvalidOperationException("This release needs a newer Cardboard Box Patcher.");
        if(r.Files.Length>15000||r.Files.Length==0)throw new InvalidDataException("Invalid file inventory.");
        CheckUri(new Uri(r.Bundle.Url));
        if(r.Bundle.Size<0||r.Bundle.Size>2_000_000_000L||!Regex.IsMatch(r.Bundle.Sha512,"^[a-f0-9]{128}$"))throw new InvalidDataException("Invalid bundle metadata.");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var f in r.Files)
        {
            ValidatePackPath(f.Path);
            if(!names.Add(f.Path)||f.Size<0||f.Size>2_000_000_000L||!Regex.IsMatch(f.Sha512,"^[a-f0-9]{128}$")||f.Mode is not ("managed" or "default" or "existing"))throw new InvalidDataException("Invalid/duplicate file: "+f.Path);
            if(f.Mode=="existing")
            {if(f.Url!=null||f.Entry!=null)throw new InvalidDataException("Existing-only file cannot contain a download source.");continue;}
            if((f.Url is null)==(f.Entry is null))throw new InvalidDataException("File needs one download source: "+f.Path);
            if(f.Url!=null)CheckUri(new Uri(f.Url));
            if(f.Entry!=null&&f.Entry!="payload/"+f.Path)throw new InvalidDataException("Unexpected archive entry.");
        }
        foreach(var f in r.Retired)
        {ValidatePackPath(f.Path);if(names.Contains(f.Path)||!Regex.IsMatch(f.Sha512,"^[a-f0-9]{128}$"))throw new InvalidDataException("Invalid retired file.");}
    }
    static IEnumerable<string> ModIds(string jar)
    {
        try
        {
            using var zip=ZipFile.OpenRead(jar);
            var entry=zip.GetEntry("META-INF/neoforge.mods.toml")??zip.GetEntry("META-INF/mods.toml");
            if(entry==null||entry.Length>1024*1024)return [];
            using var reader=new StreamReader(entry.Open());string text=reader.ReadToEnd();
            return Regex.Matches(text,@"(?ms)^\[\[mods\]\][^\[]*?^\s*modId\s*=\s*[""']([^""']+)[""']").Select(m=>m.Groups[1].Value).ToArray();
        }
        catch(InvalidDataException){return [];}
    }
    static async Task CopyLimited(Stream input,Stream output,long limit,CancellationToken ct)
    {
        byte[] buffer=new byte[131072];long written=0;int read;
        while((read=await input.ReadAsync(buffer,ct))!=0){written+=read;if(written>limit)throw new InvalidDataException("Download exceeds expected size.");await output.WriteAsync(buffer.AsMemory(0,read),ct);}
    }
    async Task Download(string url,string dest,long size,string hash,CancellationToken ct)
    {
        CheckUri(new Uri(url));Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if(File.Exists(dest)&&new FileInfo(dest).Length==size&&Hash(dest)==hash)return;
        string temp=dest+".part";
        try
        {
            await using(var output=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None,131072,true))
            {
                var uri=new Uri(url);
                if(uri.IsFile){await using var input=File.OpenRead(uri.LocalPath);await CopyLimited(input,output,size,ct);}
                else
                {
                    using var response=await Http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
                    if(response.RequestMessage?.RequestUri is {} final)CheckUri(final);
                    await using var input=await response.Content.ReadAsStreamAsync(ct);await CopyLimited(input,output,size,ct);
                }
            }
            if(new FileInfo(temp).Length!=size||Hash(temp)!=hash)throw new CryptographicException("Downloaded file failed verification: "+Path.GetFileName(dest));
            File.Move(temp,dest,true);
        }
        finally {if(File.Exists(temp))File.Delete(temp);}
    }
    static FileStream Lock(string root)
    {
        var work=Work(root);Directory.CreateDirectory(work);
        try{return new FileStream(Safe(work,"operation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException){throw new InvalidOperationException("Another patcher operation is already using this profile.");}
    }
    public static State? ReadState(string root)
    {
        string path=Safe(Work(root),"state.json");if(!File.Exists(path))return null;
        var s=Json.Read<State>(path);if(s.Product!=Product)throw new InvalidDataException("This profile belongs to another patcher.");
        foreach(var p in s.Managed.Keys)ValidatePackPath(p);return s;
    }
    public static void EnsureAppsClosed(string root)
    {
        // Query only process identity/count. Command lines may contain Minecraft tokens and must never be logged.
        var start=new ProcessStartInfo("powershell.exe") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-Command");
        string literal=root.Replace("'","''");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; $p=Get-CimInstance Win32_Process; $n=@($p | Where-Object { $_.Name -match '^Modrinth.*\\.exe$' -or ($_.Name -match '^javaw?\\.exe$' -and ($_.CommandLine -like '*"+literal+"*' -or $_.CommandLine -match '(?i)net.minecraft|cpw.mods.bootstraplauncher|--gameDir')) }).Count; Write-Output $n");
        using var proc=Process.Start(start)??throw new InvalidOperationException("Cannot check running applications.");
        string text=proc.StandardOutput.ReadToEnd();proc.WaitForExit();
        if(proc.ExitCode!=0||!int.TryParse(text.Trim(),out int count))throw new InvalidOperationException("Could not check running applications. Close Minecraft and Modrinth, then retry.");
        if(count>0)throw new InvalidOperationException("Waiting: close Minecraft and Modrinth completely before patching. Your game will not be closed automatically.");
    }
    public async Task<string> Apply(string root,Release r,CancellationToken ct=default)
    {
        Validate(r);root=Path.GetFullPath(root);
        if(!Directory.Exists(Safe(root,"mods"))||!File.Exists(Safe(root,"options.txt")))throw new InvalidOperationException("Select the existing Cardboard Box game folder imported through Modrinth.");
        guard(root);
        using var operation=Lock(root);
        Recover(root);
        var state=ReadState(root);
        if(state!=null&&(state.Minecraft!=r.Minecraft||state.NeoForge!=r.NeoForge))throw new InvalidOperationException("Minecraft/NeoForge changes need a new Modrinth import; this patcher does not edit launcher databases.");
        if(state!=null&&r.Sequence<state.Sequence)throw new InvalidOperationException("An older release was rejected. Use Restore backup for rollback.");
        if(state==null)
        {
            int matches=r.Files.Where(f=>f.Mode=="managed").Count(f=>File.Exists(Safe(root,f.Path))&&Hash(Safe(root,f.Path))==f.Sha512);
            int expected=Math.Min(3,r.Files.Count(f=>f.Mode=="managed"));
            if(matches<expected)throw new InvalidOperationException("This folder does not match Cardboard Box. Import the provided .mrpack first.");
        }
        var needed=new List<PackFile>();
        foreach(var f in r.Files)
        {
            string target=Safe(root,f.Path);
            if(f.Mode=="existing")
            {
                if(!File.Exists(target)||new FileInfo(target).Length!=f.Size||Hash(target)!=f.Sha512)
                    throw new InvalidOperationException("This release requires the matching base-pack file, which is not mirrored by the patcher: "+f.Path+". Import the supplied base .mrpack first.");
                continue;
            }
            if((f.Mode=="default"||DefaultOnly(f.Path))&&File.Exists(target))continue;
            if(!File.Exists(target)||new FileInfo(target).Length!=f.Size||Hash(target)!=f.Sha512)needed.Add(f);
        }
        var retire=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var desired=r.Files.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(state!=null)foreach(var item in state.Managed.Where(x=>!desired.Contains(x.Key)&&!DefaultOnly(x.Key)))retire[item.Key]=item.Value;
        foreach(var item in r.Retired)if(!DefaultOnly(item.Path))retire.TryAdd(item.Path,item.Sha512);
        foreach(var item in retire.ToArray())
        {
            string target=Safe(root,item.Key);
            if(!File.Exists(target)){retire.Remove(item.Key);continue;}
            if(Hash(target)!=item.Value)throw new InvalidOperationException("Obsolete file was modified locally; no changes made. Move it outside mods/config first: "+item.Key);
        }
        if(state!=null&&state.Sequence==r.Sequence&&state.Version==r.Version&&needed.Count==0&&retire.Count==0)
        {log?.Invoke("Your pack is up to date.");return "";}
        foreach(string relative in needed.Select(f=>f.Path).Concat(retire.Keys))
        {
            string target=Safe(root,relative);
            if(File.Exists(target)&&(File.GetAttributes(target)&FileAttributes.ReadOnly)!=0)
                throw new InvalidOperationException("Modrinth has marked this file read-only. No files were changed. Use an unmanaged/unlinked profile for external patching: "+relative);
        }
        string work=Work(root),session=Guid.NewGuid().ToString("N");
        string stage=Safe(work,"work/"+session);Directory.CreateDirectory(stage);
        string bundle=Safe(work,"cache/"+r.Bundle.Sha512+".zip");
        if(needed.Any(f=>f.Entry!=null))
        {log?.Invoke("Downloading pack assets…");await Download(r.Bundle.Url,bundle,r.Bundle.Size,r.Bundle.Sha512,ct);}
        using(var zip=needed.Any(f=>f.Entry!=null)?ZipFile.OpenRead(bundle):null)
        {
            for(int i=0;i<needed.Count;i++)
            {
                var f=needed[i];log?.Invoke($"Preparing {i+1}/{needed.Count}: {f.Path}");ct.ThrowIfCancellationRequested();
                string dest=Safe(stage,f.Path);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if(f.Url!=null)await Download(f.Url,dest,f.Size,f.Sha512,ct);
                else
                {
                    var entry=zip!.GetEntry(f.Entry!)??throw new InvalidDataException("Missing pack asset: "+f.Path);
                    if(entry.Length!=f.Size)throw new InvalidDataException("Archive size mismatch: "+f.Path);
                    await using(var input=entry.Open())await using(var output=File.Create(dest))await CopyLimited(input,output,f.Size,ct);
                    if(Hash(dest)!=f.Sha512)throw new CryptographicException("Archive checksum mismatch: "+f.Path);
                }
            }
        }
        ct.ThrowIfCancellationRequested();guard(root);
        // Prevent a renamed/repacked helper from coexisting with an untracked old copy.
        var providers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(string jar in Directory.GetFiles(Safe(root,"mods"),"*.jar").Where(p=>!retire.ContainsKey("mods/"+Path.GetFileName(p))&&!needed.Any(f=>f.Path.Equals("mods/"+Path.GetFileName(p),StringComparison.OrdinalIgnoreCase)))
            .Concat(needed.Where(f=>f.Path.StartsWith("mods/",StringComparison.OrdinalIgnoreCase)).Select(f=>Safe(stage,f.Path))))
            foreach(string id in ModIds(jar))
                if(!providers.TryAdd(id,Path.GetFileName(jar)))throw new InvalidOperationException("Two installed files provide mod '"+id+"': "+providers[id]+" and "+Path.GetFileName(jar)+". No pack files were changed.");
        string backup=Safe(work,"backups/"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+session);
        Directory.CreateDirectory(backup);
        string statePath=Safe(work,"state.json");var journal=new Journal{Version=r.Version,HadState=File.Exists(statePath)};
        if(journal.HadState)File.Copy(statePath,Safe(backup,"previous-state.json"));
        string journalPath=Safe(backup,"journal.json");Json.Write(journalPath,journal);
        try
        {
            int writes=0;
            foreach(string relative in needed.Select(f=>f.Path).Concat(retire.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string target=Safe(root,relative);bool exists=File.Exists(target);
                if(exists){string saved=Safe(backup,"files/"+relative);Directory.CreateDirectory(Path.GetDirectoryName(saved)!);File.Copy(target,saved);}
                journal.Changes.Add(new(relative,exists));Json.Write(journalPath,journal);
                if(retire.ContainsKey(relative))File.Delete(target);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    string temp=Safe(stage,relative);File.Move(temp,target,true); // Replace the directory entry, never write through a launcher hardlink.
                }
                AfterWrite?.Invoke(++writes);
            }
            var managed=r.Files.Where(f=>f.Mode=="managed"&&!DefaultOnly(f.Path)).ToDictionary(f=>f.Path,f=>f.Sha512,StringComparer.OrdinalIgnoreCase);
            Json.Write(statePath,new State(Product,r.Version,r.Sequence,r.Minecraft,r.NeoForge,managed));
            journal.Status="committed";Json.Write(journalPath,journal);
            log?.Invoke($"Installed {r.Version}: {needed.Count} changed files, {retire.Count} obsolete files removed. Backup retained.");
        }
        catch
        {
            Restore(root,backup,journal);journal.Status="rolled-back";Json.Write(journalPath,journal);throw;
        }
        return backup;
    }
    static void Restore(string root,string backup,Journal journal)
    {
        foreach(var c in journal.Changes.AsEnumerable().Reverse())
        {
            ValidatePackPath(c.Path);string target=Safe(root,c.Path);
            if(c.Existed)
            {
                string saved=Safe(backup,"files/"+c.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string temp=Safe(Work(root),"restore-"+Guid.NewGuid().ToString("N"));File.Copy(saved,temp);File.Move(temp,target,true);
            }
            else if(File.Exists(target))File.Delete(target);
        }
        string state=Safe(Work(root),"state.json");
        if(journal.HadState)
        {string temp=Safe(Work(root),"restore-state-"+Guid.NewGuid().ToString("N"));File.Copy(Safe(backup,"previous-state.json"),temp);File.Move(temp,state,true);}
        else if(File.Exists(state))File.Delete(state);
    }
    static IEnumerable<string> Backups(string root)
    {
        string path=Safe(Work(root),"backups");
        return Directory.Exists(path)?Directory.GetDirectories(path).OrderByDescending(p=>File.Exists(Safe(p,"journal.json"))?Json.Read<Journal>(Safe(p,"journal.json")).StartedUtcTicks:0):[];
    }
    void Recover(string root)
    {
        foreach(string backup in Backups(root))
        {
            _=Safe(Work(root),"backups/"+Path.GetFileName(backup));string path=Safe(backup,"journal.json");
            if(!File.Exists(path))continue;var journal=Json.Read<Journal>(path);
            if(journal.Status!="applying")continue;
            log?.Invoke("Recovering an interrupted update…");Restore(root,backup,journal);journal.Status="recovered";Json.Write(path,journal);
        }
    }
    public void Rollback(string root)
    {
        guard(root);using var operation=Lock(root);Recover(root);
        string? backup=Backups(root).FirstOrDefault(p=>File.Exists(Safe(p,"journal.json"))&&Json.Read<Journal>(Safe(p,"journal.json")).Status=="committed");
        if(backup==null)throw new InvalidOperationException("There is no completed update backup to restore.");
        var journal=Json.Read<Journal>(Safe(backup,"journal.json"));Restore(root,backup,journal);journal.Status="restored";Json.Write(Safe(backup,"journal.json"),journal);
        log?.Invoke("Previous pack files restored. Personal settings were left intact.");
    }
}
