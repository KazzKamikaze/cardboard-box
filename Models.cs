using System.Text.Json;

namespace CardboardPatcher;

public sealed record PackFile(string Path, long Size, string Sha512, string? Url, string? Entry, string Mode = "managed");
public sealed record RetiredFile(string Path, string Sha512);
public sealed record Bundle(string Url, long Size, string Sha512);
public sealed record Release(int Schema, string Product, string Version, long Sequence, string Minecraft, string NeoForge,
    Bundle Bundle, PackFile[] Files, RetiredFile[] Retired, string MinimumPatcher = "1.0.0");
public sealed record Envelope(string Manifest, string Signature);
public sealed record State(string Product, string Version, long Sequence, string Minecraft, string NeoForge, Dictionary<string,string> Managed);
public sealed record Change(string Path, bool Existed);
public sealed class Journal
{
    public long StartedUtcTicks { get; set; } = DateTime.UtcNow.Ticks;
    public string Status { get; set; } = "applying";
    public string Version { get; set; } = "";
    public List<Change> Changes { get; set; } = [];
    public bool HadState { get; set; }
}
public sealed class Settings
{
    public string Profile { get; set; } = "";
    public string Feed { get; set; } = Engine.DefaultFeed;
    public bool AutoInstall { get; set; } = true;
}
public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path),Options) ?? throw new InvalidDataException("Empty JSON document.");
    public static void Write<T>(string path,T data)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string temp=path+".tmp-"+Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp,JsonSerializer.SerializeToUtf8Bytes(data,Options));
        File.Move(temp,path,true);
    }
}
