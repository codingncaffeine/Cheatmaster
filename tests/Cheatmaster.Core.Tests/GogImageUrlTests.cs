using Cheatmaster.Core.Cheats;
using Xunit;

namespace Cheatmaster.Core.Tests;

/// <summary>
/// GOG image URLs cost a real game its cover once already: the host is
/// images-N.gog-statics.com rather than images.gog.com, and the background URL already carries
/// an extension, so a hard-coded host matched nothing and a blindly appended .jpg 404'd. These
/// pin the shapes that were verified to return 200.
/// </summary>
public class GogImageUrlTests
{
    private const string Hash = "9469448a4cac4901c076449035e0ac9f6ec9352959f88d8214a3c9b996ab457d";

    [Theory]
    [InlineData("//images-4.gog-statics.com/" + Hash + "_glx_logo_2x.jpg")]
    [InlineData("//images-2.gog-statics.com/" + Hash + "_glx_logo.jpg")]
    [InlineData("https://images.gog-statics.com/" + Hash + ".png")]
    [InlineData("//images.gog.com/" + Hash + "_glx_logo_2x.jpg")]
    public void Builds_portrait_covers_from_any_gog_host(string logoUrl)
    {
        var covers = GameMetadataService.BuildGogCovers(logoUrl);

        Assert.Equal(2, covers.Count);
        Assert.Equal($"https://images.gog-statics.com/{Hash}_glx_vertical_cover.jpg", covers[0]);
        Assert.Equal($"https://images.gog-statics.com/{Hash}_product_card_v2_mobile_slider_639.jpg", covers[1]);
    }

    [Fact]
    public void Builds_nothing_when_there_is_no_content_hash()
    {
        Assert.Empty(GameMetadataService.BuildGogCovers("https://example.com/not-a-hash.jpg"));
        Assert.Empty(GameMetadataService.BuildGogCovers(string.Empty));
    }

    [Theory]
    [InlineData("//images-1.gog-statics.com/abc123.jpg", "https://images-1.gog-statics.com/abc123.jpg")]
    [InlineData("https://images.gog-statics.com/abc123.png", "https://images.gog-statics.com/abc123.png")]
    [InlineData("//images.gog.com/abc123", "https://images.gog.com/abc123.jpg")]
    public void Makes_a_gog_url_fetchable_without_doubling_its_extension(string raw, string expected) =>
        Assert.Equal(expected, GameMetadataService.Normalize(raw));

    [Fact]
    public void Leaves_an_empty_url_alone() => Assert.Equal(string.Empty, GameMetadataService.Normalize(string.Empty));
}
