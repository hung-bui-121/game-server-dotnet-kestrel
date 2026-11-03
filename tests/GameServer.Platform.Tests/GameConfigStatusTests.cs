using GameServer.Config;
using GameServer.Platform.Config;
using Shouldly;

namespace GameServer.Platform.Tests;

/// <summary>FR-032 (slice 015 T030): the config status is published once and no member ever throws.</summary>
public sealed class GameConfigStatusTests
{
    private static readonly IReadOnlyDictionary<Type, object> NoCatalogs = new Dictionary<Type, object>();

    [Fact]
    public void Before_publication_the_status_is_not_loaded()
    {
        var status = new GameConfigStatus();

        status.State.ShouldBe(GameConfigState.NotLoaded);
        status.Config.ShouldBeNull();
        status.DeclarationsPassed.ShouldBeFalse();
        status.TryGetConfig(out var config).ShouldBeFalse();
        config.ShouldBeNull();
        status.TryGetCatalog<CatalogA>(out var catalog).ShouldBeFalse();
        catalog.ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Loaded_gives_the_instance_and_whether_the_declarations_passed(bool passed)
    {
        var status = new GameConfigStatus();
        var config = Empty();

        status.PublishLoaded(config, passed, NoCatalogs);

        status.State.ShouldBe(GameConfigState.Loaded);
        status.Config.ShouldBeSameAs(config);
        status.DeclarationsPassed.ShouldBe(passed);
        status.TryGetConfig(out var read).ShouldBeTrue();
        read.ShouldBeSameAs(config);
    }

    [Fact]
    public void Failed_has_no_config_and_does_not_throw()
    {
        var status = new GameConfigStatus();

        status.PublishFailed();

        status.State.ShouldBe(GameConfigState.Failed);
        status.Config.ShouldBeNull();
        status.DeclarationsPassed.ShouldBeFalse();
        status.TryGetConfig(out _).ShouldBeFalse();
        status.TryGetCatalog<CatalogA>(out _).ShouldBeFalse();
    }

    // ---- slice 015 T071 G1: a startup check reads a declared catalog through the status ----

    [Fact]
    public void A_published_catalog_is_the_instance_and_any_other_type_is_false()
    {
        var status = new GameConfigStatus();
        var built = new CatalogA();
        var catalogs = new Dictionary<Type, object> { [typeof(CatalogA)] = built };

        status.PublishLoaded(Empty(), declarationsPassed: false, catalogs);
        catalogs.Clear();

        status.TryGetCatalog<CatalogA>(out var catalog).ShouldBeTrue();
        catalog.ShouldBeSameAs(built);
        status.TryGetCatalog<CatalogB>(out var other).ShouldBeFalse();
        other.ShouldBeNull();
    }

    [Fact]
    public void A_second_publication_is_refused_and_keeps_the_first()
    {
        var status = new GameConfigStatus();
        status.PublishFailed();

        Should.Throw<InvalidOperationException>(() => status.PublishLoaded(Empty(), declarationsPassed: true, NoCatalogs));
        Should.Throw<InvalidOperationException>(status.PublishFailed);
        status.State.ShouldBe(GameConfigState.Failed);
    }

    private static IGameConfig Empty() => new GameConfig("test", new Dictionary<string, byte[]>(), []);

    private sealed class CatalogA;

    private sealed class CatalogB;
}
