using GameServer.Platform.Config;
using Shouldly;

namespace GameServer.Platform.Tests.Config;

/// <summary>Data-model §3.4 (slice 015 T012): the unavailability marker is found anywhere in the inner-exception chain.</summary>
public sealed class UnavailableTests
{
    [Fact]
    public void The_marker_is_found_at_depth_zero_one_and_two()
    {
        var marked = Unavailable.Mark(new GameServerStartupException("[config] x"), Unavailable.GameConfig);

        Unavailable.Find(marked).ShouldBe("game config");
        Unavailable.Find(new InvalidOperationException("outer", marked)).ShouldBe("game config");
        Unavailable.Find(new InvalidOperationException("outer", new InvalidOperationException("middle", marked))).ShouldBe("game config");
    }

    [Fact]
    public void No_marker_gives_null_and_other_data_keys_are_ignored()
    {
        var plain = new InvalidOperationException("x");
        plain.Data["Other"] = "game config";

        Unavailable.Find(plain).ShouldBeNull();
        Unavailable.Find(null).ShouldBeNull();
    }

    [Fact]
    public void A_catalog_marker_names_the_type()
    {
        var marker = Unavailable.Catalog(typeof(SampleCatalog));

        marker.ShouldBe("catalog SampleCatalog");
        Unavailable.CatalogTypeName(marker).ShouldBe("SampleCatalog");
        Unavailable.CatalogTypeName(Unavailable.Provision).ShouldBeNull();
    }

    private sealed class SampleCatalog;
}
