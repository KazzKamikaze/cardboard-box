using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace CardboardPatcher;

static class Publisher
{
    public static void Build(string archive,string output,string privateKey,string baseUrl,long sequence)
    {
        Directory.CreateDirectory(output);
        string bundlePath=Path.Combine(output,"cardboard-assets.zip");
        if(File.Exists(bundlePath))throw new IOException("Release output already exists; never replace an immutable release.");
        var files=new List<PackFile>();
        using(var source=ZipFile.OpenRead(archive))
        using(var bundle=ZipFile.Open(bundlePath,ZipArchiveMode.Create))
        {
            using var doc=JsonDocument.Parse(source.GetEntry("modrinth.index.json")!.Open());var index=doc.RootElement;
            string version=index.GetProperty("versionId").GetString()!;
            foreach(var f in index.GetProperty("files").EnumerateArray())
            {
                string path=f.GetProperty("path").GetString()!;Engine.ValidatePackPath(path);
                files.Add(new(path,f.GetProperty("fileSize").GetInt64(),f.GetProperty("hashes").GetProperty("sha512").GetString()!,f.GetProperty("downloads")[0].GetString(),null));
            }
            foreach(var entry in source.Entries.Where(e=>e.FullName.StartsWith("client-overrides/")&&!e.FullName.EndsWith('/')))
            {
                string path=entry.FullName["client-overrides/".Length..];Engine.ValidatePackPath(path);
                using var memory=new MemoryStream();using(var input=entry.Open())input.CopyTo(memory);byte[] data=memory.ToArray();
                // Keep restricted/unresolved custom third-party binaries on the recipient's existing base installation.
                // Do not mirror them into a public GitHub asset merely because they were in a private export.
                if(path.StartsWith("mods/")&&new[]{"OctoLib-","neo-voxy-","drippyloadingscreen-earlywindow_","moreculling-"}.Any(prefix=>Path.GetFileName(path).StartsWith(prefix,StringComparison.OrdinalIgnoreCase)))
                {files.Add(new(path,data.LongLength,Engine.Hash(data),null,null,"existing"));continue;}
                // Correct stale human-readable bookkeeping in the supplied snapshot.
                if(path=="CARDBOARD-BOX-README.txt")
                {
                    string text=System.Text.Encoding.UTF8.GetString(data);
                    text=text.Replace("264 active mod JARs","260 active mod JARs");
                    text=text.Replace("Inventory Profiles Next's two corner shortcuts and hotbar auto-refill badges\nare hidden; normal sorting and refill remain available.","Mouse Tweaks handles inventory mouse controls; Inventory Profiles Next is removed.");
                    text+="\nUPDATES\nRun Cardboard Box Patcher with Minecraft and Modrinth closed. Approved pack releases are checked automatically. Personal settings and worlds are preserved.\n";
                    data=System.Text.Encoding.UTF8.GetBytes(text);
                }
                if(path=="CARDBOARD-BOX-COMPONENTS.json")
                {
                    using var components=JsonDocument.Parse(data);
                    if(components.RootElement.ValueKind==JsonValueKind.Array)
                    {
                        var actual=files.Select(f=>f.Path).Concat(source.Entries.Where(e=>e.FullName.StartsWith("client-overrides/mods/")).Select(e=>e.FullName["client-overrides/".Length..])).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        data=JsonSerializer.SerializeToUtf8Bytes(components.RootElement.EnumerateArray().Where(e=>!e.TryGetProperty("path",out var p)||actual.Contains(p.GetString()!)).ToArray(),Json.Options);
                    }
                }
                var target=bundle.CreateEntry("payload/"+path,CompressionLevel.Fastest);using(var stream=target.Open())stream.Write(data);
                files.Add(new(path,data.LongLength,Engine.Hash(data),null,"payload/"+path,Engine.DefaultOnly(path)?"default":"managed"));
            }
            // Retire only the exact known old files; locally modified or unrelated files are never silently removed.
            var retired=new List<RetiredFile>();
            string[] names={"InventoryProfilesNext-neoforge-1.21.1-2.2.5.jar","libIPN-neoforge-1.21.1-6.6.3.jar","inventoryessentials-neoforge-1.21.1-21.1.15.jar","moremousetweaks-neoforge-1.1.1+1.21.1.jar","minevolts-presentation-0.2.9.jar"};
            string retiredRoot=Environment.GetEnvironmentVariable("CARDBOARD_RETIRED_MODS")??"";
            foreach(string name in names)
                if(File.Exists(Path.Combine(retiredRoot,name)))retired.Add(new("mods/"+name,Engine.Hash(Path.Combine(retiredRoot,name))));
            if(files.Any(f=>f.Path.StartsWith("mods/alltheleaks-")))
            {
                string licenseFile=Environment.GetEnvironmentVariable("CARDBOARD_ATL_LICENSE")??throw new InvalidDataException("Provide the upstream AllTheLeaks MIT notice when publishing its compatibility build.");
                byte[] notice=File.ReadAllBytes(licenseFile);string path="licenses/AllTheLeaks-MIT.txt";
                using(var outputNotice=bundle.CreateEntry("payload/"+path).Open())outputNotice.Write(notice);
                files.Add(new(path,notice.LongLength,Engine.Hash(notice),null,"payload/"+path));
            }
            // Bundle must be closed before hashing it.
            bundle.Dispose();
            var deps=index.GetProperty("dependencies");
            var release=new Release(1,Engine.Product,version,sequence,deps.GetProperty("minecraft").GetString()!,deps.GetProperty("neoforge").GetString()!,new(baseUrl.TrimEnd('/')+"/cardboard-assets.zip",new FileInfo(bundlePath).Length,Engine.Hash(bundlePath)),files.ToArray(),retired.ToArray());
            new Engine().Validate(release);
            byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(release,Json.Options);
            using var key=ECDsa.Create();key.ImportFromPem(File.ReadAllText(privateKey));
            var envelope=new Envelope(Convert.ToBase64String(bytes),Convert.ToBase64String(key.SignData(bytes,HashAlgorithmName.SHA256)));
            Json.Write(Path.Combine(output,"cardboard-release.json"),envelope);
            Json.Write(Path.Combine(output,"release-inventory.json"),new {release.Version,release.Sequence,files=files.Count,mods=files.Count(f=>f.Path.StartsWith("mods/")),retired=retired.Count,bundleBytes=new FileInfo(bundlePath).Length});
        }
    }
}
