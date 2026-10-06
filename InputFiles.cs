public static class InputFiles
{
    public static string RequireFile(string path, string extension, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The {description} must have a {extension} extension.", nameof(path));
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The {description} was not found.", fullPath);
        }

        return fullPath;
    }

    public static string RequireTrainingDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Training-sample directory not found: {fullPath}");
        }

        if (!Directory.EnumerateDirectories(fullPath).Any())
        {
            throw new InvalidOperationException($"The training-sample directory contains no sample folders: {fullPath}");
        }

        return fullPath;
    }
}
