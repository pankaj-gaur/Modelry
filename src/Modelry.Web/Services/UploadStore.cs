namespace Modelry.Web.Services;

/// <summary>Holds uploaded IA workbooks between dry run and execution (temp folder, cleaned after 24h).</summary>
public sealed class UploadStore
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "modelry-uploads");

    public UploadStore()
    {
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.GetFiles(_dir).Where(f => File.GetCreationTimeUtc(f) < DateTime.UtcNow.AddHours(-24)))
            try { File.Delete(f); } catch { /* ignore */ }
    }

    public async Task<string> SaveAsync(IFormFile file)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var fs = File.Create(Path.Combine(_dir, id + ".xlsx"));
        await file.CopyToAsync(fs);
        return id;
    }

    public Stream Open(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid upload id.");
        return File.OpenRead(Path.Combine(_dir, id + ".xlsx"));
    }
}
