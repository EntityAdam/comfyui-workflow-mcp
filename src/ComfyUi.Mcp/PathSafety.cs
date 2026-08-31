namespace ComfyUi.Mcp;

public static class PathSafety
{
    public static bool IsWithin(string childPath, string parentPath)
    {
        var child = Path.GetFullPath(childPath);
        var parent = Path.GetFullPath(parentPath);
        var relative = Path.GetRelativePath(parent, child);
        if (relative == ".") return true;
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            && !relative.StartsWith(
                ".." + Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal);
    }
}
