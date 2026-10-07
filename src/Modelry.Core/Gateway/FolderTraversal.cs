namespace Modelry.Core.Gateway;

public sealed record FolderEntry(string Id, string Title, string RelativePath);
public sealed record SchemaLocation(string Id, string Title, string Purpose, string FolderId, string RelativePath);

/// <summary>Traversal helpers built on the gateway primitives (paths use '/', '.' = start folder).</summary>
public static class FolderTraversal
{
    public static string Combine(string parent, string child) => parent == "." ? child : parent + "/" + child;

    public static async Task<List<FolderEntry>> GetFoldersAsync(ITridionGateway gw, string startFolderId, string startTitle, bool recursive,
        Action<int>? onFolder = null)
    {
        var result = new List<FolderEntry> { new(startFolderId, startTitle, ".") };
        if (!recursive) return result;
        var queue = new Queue<FolderEntry>(result);
        while (queue.Count > 0)
        {
            var f = queue.Dequeue();
            foreach (var sub in await gw.GetSubFoldersAsync(f.Id))
            {
                var e = new FolderEntry(sub.Id, sub.Title, Combine(f.RelativePath, sub.Title));
                result.Add(e); queue.Enqueue(e);
                onFolder?.Invoke(result.Count);
            }
        }
        return result;
    }

    public static async Task<List<SchemaLocation>> GetSchemasAsync(ITridionGateway gw, IEnumerable<FolderEntry> folders,
        Action<int, int, int>? onFolder = null)
    {
        var list = new List<SchemaLocation>();
        var all = folders.ToList();
        for (var i = 0; i < all.Count; i++)
        {
            var f = all[i];
            foreach (var s in await gw.GetSchemasInFolderAsync(f.Id))
                list.Add(new SchemaLocation(s.Id, s.Title, s.Purpose, f.Id, f.RelativePath));
            onFolder?.Invoke(i + 1, all.Count, list.Count);
        }
        return list;
    }
}
