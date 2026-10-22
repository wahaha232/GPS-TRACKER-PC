using GpsTrackerPc.Core.Updates;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class ChangelogDocumentTests
{
    private const string Changelog = """
        # CHANGELOG

        ## 0.2.0
        - 日期：2026-10-22
        - 新增雲端更新畫面

        ## 0.1.0
        - 日期：2026-10-22
        - 初版
        """;

    [Fact]
    public void ExtractsOnlyTheRequestedVersionSection()
    {
        var section = ChangelogDocument.GetSection(Changelog, "0.2.0");

        Assert.NotNull(section);
        Assert.Contains("## 0.2.0", section);
        Assert.Contains("新增雲端更新畫面", section);
        Assert.DoesNotContain("初版", section);
    }

    [Fact]
    public void AcceptsVersionWithLeadingVAndBrackets()
    {
        const string changelog = """
            ## [0.3.0]
            - 日期：2026-10-23
            - 測試
            """;

        var section = ChangelogDocument.GetSection(changelog, "v0.3.0");

        Assert.Contains("[0.3.0]", section);
    }

    [Fact]
    public void MissingVersionReturnsNull()
    {
        Assert.Null(ChangelogDocument.GetSection(Changelog, "9.9.9"));
        Assert.Null(ChangelogDocument.GetSection(null, "0.2.0"));
    }
}
