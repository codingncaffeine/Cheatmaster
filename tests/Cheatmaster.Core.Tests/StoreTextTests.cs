using Cheatmaster.Core.Cheats;
using Xunit;

namespace Cheatmaster.Core.Tests;

/// <summary>
/// Store descriptions are HTML, and stripping only the angle brackets leaves the entities and
/// layout whitespace behind — which is how a description ends up reading "&amp;nbsp; &amp;nbsp;"
/// on screen.
/// </summary>
public class StoreTextTests
{
    [Fact]
    public void Decodes_entities_rather_than_showing_them()
    {
        string cleaned = GameMetadataService.StripHtml("&nbsp;\n\n&nbsp;\n\nBuild a dungeon &amp; profit.");

        Assert.DoesNotContain("&nbsp;", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("&amp;", cleaned, StringComparison.Ordinal);
        Assert.Equal("Build a dungeon & profit.", cleaned);
    }

    [Fact]
    public void Removes_tags_without_gluing_words_together()
    {
        Assert.Equal("one two", GameMetadataService.StripHtml("<p>one</p><p>two</p>"));
    }

    [Fact]
    public void Collapses_layout_whitespace()
    {
        Assert.Equal("a b", GameMetadataService.StripHtml("a \n\n\n   \t b"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Handles_nothing_gracefully(string input) =>
        Assert.Equal(string.Empty, GameMetadataService.StripHtml(input));

    [Fact]
    public void Leaves_plain_text_alone()
    {
        const string plain = "A tycoon game about dungeons.";
        Assert.Equal(plain, GameMetadataService.StripHtml(plain));
    }
}
