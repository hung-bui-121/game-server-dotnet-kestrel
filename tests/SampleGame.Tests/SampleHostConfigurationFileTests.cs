using System.Text.Json.Nodes;
using GameServer.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>
/// Slice 011 US3 (research R3): the committed sample host configuration files are safe in every flavor. They are read from the
/// source tree (not the copied output), and every <c>samples/SampleHost/appsettings*.json</c> is covered, so a new file is checked
/// automatically. Rules: no secret in any file (FR-006, US3-8), a clean shared base (FR-005), the flavor table (FR-002, US3-1),
/// the GM staff seed only in Development flavors (R3), the local URL (FR-033) and the launch profiles that carry it (FR-034).
/// </summary>
[Collection(ConsoleErrorCollection.Name)]
public sealed class SampleHostConfigurationFileTests
{
    private const string BaseFile = "appsettings.json";
    private const string LocalFile = "appsettings.Local.json";
    private const int LocalPort = 5080;
    private const string LaunchSettingsFile = "Properties/launchSettings.json";

    private static readonly string SampleHostFolder = Path.Combine(RepositoryRoot(), "samples", "SampleHost");

    public static TheoryData<string> Files()
    {
        var files = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(SampleHostFolder, "appsettings*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal))
        {
            files.Add(file!);
        }

        return files;
    }

    public static TheoryData<string, string> StandardFlavorFiles()
    {
        var flavors = new TheoryData<string, string>();
        foreach (var environmentId in StandardFlavors.DeploymentEnvironmentById.Keys)
        {
            flavors.Add($"appsettings.{char.ToUpperInvariant(environmentId[0])}{environmentId[1..]}.json", environmentId);
        }

        return flavors;
    }

    [Fact]
    public void The_sample_host_has_the_base_file_and_one_file_per_standard_flavor()
    {
        var files = Files().Select(row => row.Data).ToArray();

        files.ShouldContain(BaseFile);
        foreach (var flavor in StandardFlavorFiles())
        {
            files.ShouldContain(flavor.Data.Item1);
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void No_file_holds_a_secret(string file)
    {
        var configuration = Load(file);

        foreach (var key in new GameServerOptions().SecretKeys)
        {
            configuration[key].ShouldBeNullOrEmpty($"{file} sets the secret {key}: it must come from the environment (OPS-08).");
        }
    }

    [Fact]
    public void The_base_file_holds_no_flavor_value()
    {
        var configuration = Load(BaseFile);

        foreach (var key in new[] { "GameServer:DeploymentEnvironment", "GameServer:EnvironmentId", "GameServer:Store", "Gm:Bootstrap", "Urls", "FileStore" })
        {
            configuration.GetSection(key).Exists().ShouldBeFalse($"{BaseFile} sets {key}, which belongs in a flavor file (FR-005).");
        }

        configuration["GameServer:GameId"].ShouldBe("sample");
    }

    [Theory]
    [MemberData(nameof(StandardFlavorFiles))]
    public void Each_standard_flavor_file_carries_its_table_values(string file, string environmentId)
    {
        var configuration = Load(file);

        configuration["GameServer:EnvironmentId"].ShouldBe(environmentId);
        configuration["GameServer:DeploymentEnvironment"].ShouldBe(StandardFlavors.DeploymentEnvironmentById[environmentId]);
        configuration["GameServer:Store"].ShouldBe(environmentId == "local" ? nameof(StoreKind.File) : nameof(StoreKind.Couchbase));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void The_gm_staff_seed_exists_only_in_development_flavors(string file)
    {
        var configuration = Load(file);

        if (configuration.GetSection("Gm:Bootstrap").Exists())
        {
            configuration["GameServer:DeploymentEnvironment"].ShouldBe(
                DeploymentEnvironments.Development,
                $"{file} seeds GM staff with a committed password: only Development flavors may (research R3).");
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Only_the_local_flavor_sets_urls_and_it_is_loopback_on_5080(string file)
    {
        var urls = Load(file)["Urls"];

        if (file != LocalFile)
        {
            urls.ShouldBeNull($"{file} sets Urls; only {LocalFile} may (FR-033).");
            return;
        }

        urls.ShouldNotBeNullOrWhiteSpace();
        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var uri = new Uri(url);
            uri.IsLoopback.ShouldBeTrue($"{url} is not a loopback address (FR-033).");
            uri.Port.ShouldBe(LocalPort);
        }
    }

    [Fact]
    public void The_urls_switch_beats_the_local_flavor_file()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = ["--urls", "http://localhost:5999"],
            EnvironmentName = "Local",
            ContentRootPath = SampleHostFolder,
        });

        // The local file sets Urls (5080); the script's and the developer's --urls wins (US1-4).
        using var configuration = builder.Configuration;
        configuration["urls"].ShouldBe("http://localhost:5999");
    }

    [Fact]
    public void The_launch_profiles_are_local_first_then_dev_couchbase_on_the_local_url()
    {
        var profiles = JsonNode.Parse(File.ReadAllText(Path.Combine(SampleHostFolder, LaunchSettingsFile)))!["profiles"]!.AsObject();
        var localUrls = Load(LocalFile)["Urls"];

        // The first profile is the default of dotnet run and the IDE (FR-034).
        profiles.Select(p => p.Key).ShouldBe(["local", "dev-couchbase"]);
        profiles["local"]!["environmentVariables"]!["ASPNETCORE_ENVIRONMENT"]!.GetValue<string>().ShouldBe("Local");
        profiles["dev-couchbase"]!["environmentVariables"]!["ASPNETCORE_ENVIRONMENT"]!.GetValue<string>().ShouldBe("Dev");
        foreach (var (name, profile) in profiles)
        {
            profile!["applicationUrl"]!.GetValue<string>().ShouldBe(localUrls, $"profile {name} and {LocalFile} must carry the same URL.");
            profile.AsObject().ToString().ShouldNotContain("Password", Case.Insensitive, $"profile {name} must not carry a secret (OPS-08).");
        }
    }

    [Fact]
    public async Task A_production_host_without_a_flavor_file_fails_closed()
    {
        var error = await ConsoleErrorCapture.RunAsync(async () =>
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Production"));

            // The entry point returns 1 before running the host; the factory then reports a host that never started.
            Should.Throw<InvalidOperationException>(() => factory.CreateClient());
        });

        error.ShouldContain("GameServer:EnvironmentId");
        error.ShouldContain("[deployment] the InMemory store is configured in 'Production'");
        error.ShouldNotContain("'Development'");
    }

    private static IConfigurationRoot Load(string file) =>
        new ConfigurationBuilder().AddJsonFile(Path.Combine(SampleHostFolder, file), optional: false).Build();

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GameServer.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"GameServer.slnx not found above {AppContext.BaseDirectory}.");
    }
}
