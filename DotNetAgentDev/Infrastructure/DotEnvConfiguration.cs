namespace DotNetAgentDev.Infrastructure;

public static class DotEnvConfiguration
{
    public static string? FindFile(string contentRootPath, string currentDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(currentDirectory, ".env"),
            Path.Combine(contentRootPath, ".env"),
            Path.Combine(Directory.GetParent(contentRootPath)?.FullName ?? contentRootPath, ".env")
        };

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    public static IReadOnlyDictionary<string, string?> Load(string path)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line[7..].TrimStart();
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            var value = ParseValue(line[(separatorIndex + 1)..]);
            var configurationKey = key.Replace("__", ":", StringComparison.Ordinal);
            values[configurationKey] = value;
        }

        return values;
    }

    private static string ParseValue(string rawValue)
    {
        var value = rawValue.Trim();
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        var commentIndex = FindInlineComment(value);
        return commentIndex >= 0
            ? value[..commentIndex].TrimEnd()
            : value;
    }

    private static int FindInlineComment(string value)
    {
        for (var index = 1; index < value.Length; index++)
        {
            if (value[index] == '#' && char.IsWhiteSpace(value[index - 1]))
            {
                return index;
            }
        }

        return -1;
    }
}
