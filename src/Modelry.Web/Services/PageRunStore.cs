using System.Collections.Concurrent;
using System.IO.Compression;

namespace Modelry.Web.Services;

/// <summary>The user's choice for one section on the validation screen.</summary>
public sealed record SectionChoice(string Value);

/// <summary>One page's HTML zip, extracted for the Pages step. Files live in a temp folder for 2 hours.</summary>
public sealed class PageRun
{
    public required string Id { get; init; }
    public required string Owner { get; init; }
    public required string PageId { get; init; }
    public required string Folder { get; init; }
    /// <summary>Entry HTML file, relative to Folder, with forward slashes.</summary>
    public required string EntryPath { get; init; }
    public required string ZipName { get; init; }
    public bool AllowScripts { get; set; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public int FileCount { get; init; }
    public long Bytes { get; init; }
    public List<string> IgnoredFiles { get; init; } = new();
    /// <summary>Validation-screen overrides by section index: "map:MAP-031", "ct:&lt;template title&gt;" or "exclude".</summary>
    public ConcurrentDictionary<int, string> Choices { get; } = new();
    public string? ViewsResultId { get; set; }
    /// <summary>Element paths of the sections from the last analysis (after any splitting), in section order, for the preview.</summary>
    public List<string> SectionPaths { get; set; } = new();
    public string EntryFullPath => Path.Combine(Folder, EntryPath.Replace('/', Path.DirectorySeparatorChar));
}

public sealed class PageZipException : Exception
{
    public List<string> HtmlFiles { get; }
    public PageZipException(string message, List<string>? htmlFiles = null) : base(message) => HtmlFiles = htmlFiles ?? new();
}

/// <summary>
/// Extracts page zips safely (no paths outside the run folder, size and file limits, web file types only) and keeps them
/// for 2 hours. The run id is random and doubles as the preview URL's access key, because the sandboxed preview frames
/// send no session cookie.
/// </summary>
public sealed class PageRunStore
{
    public const long MaxBytes = 100L * 1024 * 1024;
    public const int MaxFiles = 2000;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".css", ".js", ".mjs", ".map", ".json", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".avif", ".ico", ".bmp",
        ".woff", ".woff2", ".ttf", ".otf", ".eot", ".mp4", ".webm", ".ogg", ".mp3", ".vtt", ".pdf", ".doc", ".docx", ".xls", ".xlsx",
        ".ppt", ".pptx", ".csv", ".txt", ".xml"
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "modelry-pages");
    private readonly ConcurrentDictionary<string, PageRun> _runs = new();
    private readonly ILogger<PageRunStore> _log;

    public PageRunStore(ILogger<PageRunStore> log)
    {
        _log = log;
        Directory.CreateDirectory(_root);
    }

    public PageRun? Get(string id)
    {
        Purge();
        return _runs.TryGetValue(id, out var r) ? r : null;
    }

    public PageRun? Get(string id, string owner) => Get(id) is { } r && r.Owner == owner ? r : null;

    public async Task<PageRun> CreateAsync(IFormFile zip, string owner, string pageId, string? entryHint, bool allowScripts)
    {
        Purge();
        if (!zip.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new PageZipException("Upload the page as a .zip file.");
        if (zip.Length > MaxBytes) throw new PageZipException($"The zip is larger than {MaxBytes / 1024 / 1024} MB.");
        var id = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(_root, id);
        Directory.CreateDirectory(folder);
        var rootFull = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        var ignored = new List<string>();
        var html = new List<string>();
        int files = 0; long bytes = 0;
        try
        {
            await using var input = zip.OpenReadStream();
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.Length == 0 && entry.Name.Length == 0) continue;
                var name = entry.FullName.Replace('\\', '/');
                if (name.Split('/').Any(p => p.StartsWith('.') || p == "__MACOSX")) continue;
                if (!Allowed.Contains(Path.GetExtension(name))) { ignored.Add(name); continue; }
                var target = Path.GetFullPath(Path.Combine(folder, name));
                if (!target.StartsWith(rootFull, StringComparison.Ordinal)) throw new PageZipException($"The zip contains a path outside its folder ('{name}').");
                if (++files > MaxFiles) throw new PageZipException($"The zip has more than {MaxFiles} files.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var src = entry.Open())
                await using (var dst = File.Create(target))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await src.ReadAsync(buffer)) > 0)
                    {
                        bytes += read;
                        if (bytes > MaxBytes) throw new PageZipException($"The unpacked files are larger than {MaxBytes / 1024 / 1024} MB.");
                        await dst.WriteAsync(buffer.AsMemory(0, read));
                    }
                }
                if (Path.GetExtension(name) is ".html" or ".htm") html.Add(name);
            }
        }
        catch (InvalidDataException) { Delete(folder); throw new PageZipException("The file is not a readable zip."); }
        catch { Delete(folder); throw; }

        string entryPath;
        if (!string.IsNullOrWhiteSpace(entryHint) && html.FirstOrDefault(h => h.Equals(entryHint.Trim().Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) is { } chosen)
            entryPath = chosen;
        else if (html.Where(h => Path.GetFileName(h).Equals("index.html", StringComparison.OrdinalIgnoreCase)).OrderBy(h => h.Count(c => c == '/')).FirstOrDefault() is { } index)
            entryPath = index;
        else if (html.Count == 1) entryPath = html[0];
        else
        {
            Delete(folder);
            throw html.Count == 0
                ? new PageZipException("The zip has no .html file.")
                : new PageZipException("The zip has several HTML files and no index.html. Choose the page below and upload again.", html.OrderBy(h => h).ToList());
        }

        var run = new PageRun
        {
            Id = id, Owner = owner, PageId = pageId, Folder = folder, EntryPath = entryPath, ZipName = zip.FileName,
            AllowScripts = allowScripts, FileCount = files, Bytes = bytes, IgnoredFiles = ignored
        };
        _runs[id] = run;
        return run;
    }

    /// <summary>Full path of a file in the run, or null when the path is outside it or missing.</summary>
    public static string? Resolve(PageRun run, string relative)
    {
        var rootFull = Path.GetFullPath(run.Folder) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(run.Folder, Uri.UnescapeDataString(relative).Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(rootFull, StringComparison.Ordinal) && File.Exists(full) ? full : null;
    }

    private void Purge()
    {
        foreach (var (id, run) in _runs)
            if (DateTime.UtcNow - run.CreatedUtc > Lifetime && _runs.TryRemove(id, out _)) Delete(run.Folder);
    }

    private void Delete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not delete page run folder {Folder}", folder); }
    }
}
