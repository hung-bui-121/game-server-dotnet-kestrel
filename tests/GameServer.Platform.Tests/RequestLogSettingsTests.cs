using Shouldly;

namespace GameServer.Platform.Tests;

/// <summary>Slice 013 T036 (FR-086): defaults per deployment environment and the startup rules of <c>GameServer:RequestLog</c>.</summary>
public sealed class RequestLogSettingsTests
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("Test", true)]
    [InlineData("Staging", false)]
    [InlineData("Production", false)]
    public void Unset_follows_the_deployment_environment(string environment, bool expected)
    {
        var options = new GameServerOptions { DeploymentEnvironment = environment };

        options.RequestLog.Enabled.ShouldBeNull();
        options.RequestLog.IsEnabled(options).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void An_explicit_value_wins_everywhere(string environment)
    {
        var options = new GameServerOptions { DeploymentEnvironment = environment };

        options.RequestLog.Enabled = true;
        options.RequestLog.IsEnabled(options).ShouldBeTrue();
        options.RequestLog.Enabled = false;
        options.RequestLog.IsEnabled(options).ShouldBeFalse();
    }

    [Fact]
    public void Defaults_are_valid_with_no_exclusions()
    {
        var settings = new RequestLogSettings();

        settings.ExcludedPaths.ShouldBeEmpty();
        settings.Validate().ShouldBeEmpty();
    }

    [Fact]
    public void Valid_entries_pass()
    {
        var settings = new RequestLogSettings { ExcludedPaths = ["/v1/time", "/api/v1/player/snapshot", "/" + new string('a', 199)] };

        settings.Validate().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("v1/time", "ExcludedPaths:0 'v1/time' must start with '/'")]
    [InlineData("", "ExcludedPaths:0 '' must start with '/'")]
    [InlineData("/v1/time?x=1", "ExcludedPaths:0 '/v1/time?x=1' must be a path without a query string")]
    public void Invalid_entries_are_reported(string entry, string expected)
    {
        var errors = new RequestLogSettings { ExcludedPaths = [entry] }.Validate().ToList();

        errors.ShouldHaveSingleItem().ShouldStartWith("[request-log] GameServer:RequestLog:");
        errors[0].ShouldContain(expected);
    }

    [Fact]
    public void An_entry_longer_than_200_characters_is_reported_without_echoing_it()
    {
        var entry = "/" + new string('a', RequestLogSettings.MaxExcludedPathLength);

        var error = new RequestLogSettings { ExcludedPaths = [entry] }.Validate().ShouldHaveSingleItem();

        error.ShouldBe("[request-log] GameServer:RequestLog:ExcludedPaths:0 has 201 characters; at most 200 are allowed.");
    }

    [Fact]
    public void More_than_50_entries_are_reported_once_and_each_bad_entry_by_index()
    {
        var entries = Enumerable.Range(0, RequestLogSettings.MaxExcludedPaths + 1).Select(i => "/p" + i).ToArray();
        entries[7] = "bad";

        var errors = new RequestLogSettings { ExcludedPaths = entries }.Validate().ToList();

        errors.Count.ShouldBe(2);
        errors[0].ShouldBe("[request-log] GameServer:RequestLog:ExcludedPaths has 51 entries; at most 50 are allowed.");
        errors[1].ShouldContain("ExcludedPaths:7 'bad' must start with '/'");
    }
}
