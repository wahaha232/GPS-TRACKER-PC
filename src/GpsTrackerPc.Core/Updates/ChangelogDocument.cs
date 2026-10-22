using System.Text;
using System.Text.RegularExpressions;

namespace GpsTrackerPc.Core.Updates;

/// <summary>從 CHANGELOG.md 取出指定版本的段落，供 Release 說明與測試共用。</summary>
public static class ChangelogDocument
{
    public static string? GetSection(string? changelog, string version)
    {
        if (string.IsNullOrWhiteSpace(changelog) || string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var normalized = version.Trim().TrimStart('v');
        var headingPattern = new Regex(
            @"^##\s+\[?" + Regex.Escape(normalized) + @"\]?(\s|$)",
            RegexOptions.CultureInvariant);

        var lines = changelog.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var start = -1;
        for (var index = 0; index < lines.Length; index++)
        {
            if (headingPattern.IsMatch(lines[index].TrimEnd()))
            {
                start = index;
                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        for (var index = start; index < lines.Length; index++)
        {
            if (index > start && lines[index].StartsWith("## ", StringComparison.Ordinal))
            {
                break;
            }

            builder.AppendLine(lines[index]);
        }

        var section = builder.ToString().Trim();
        return section.Length == 0 ? null : section;
    }
}
