using System.Reflection;
using Shouldly;

namespace GameServer.Platform.Tests;

/// <summary>Slice 013 T060 (FR-052, LOG-03): the instance-id rule and the version read of an assembly.</summary>
public sealed class GameServerRuntimeInfoTests
{
    [Fact]
    public void The_configured_instance_id_wins_then_hostname_then_the_machine_name()
    {
        string? Hostname(string name) => name == "HOSTNAME" ? "pod-7" : null;
        string? NoHostname(string name) => name == "HOSTNAME" ? string.Empty : null;

        GameServerRuntimeInfo.ResolveInstanceId("host-1", Hostname).ShouldBe("host-1");
        GameServerRuntimeInfo.ResolveInstanceId(null, Hostname).ShouldBe("pod-7");
        GameServerRuntimeInfo.ResolveInstanceId("  ", Hostname).ShouldBe("pod-7");
        GameServerRuntimeInfo.ResolveInstanceId(null, NoHostname).ShouldBe(Environment.MachineName);
        GameServerRuntimeInfo.ResolveInstanceId(null, _ => null).ShouldBe(Environment.MachineName);
    }

    [Fact]
    public void Of_is_the_semver_core_and_informational_of_keeps_the_sha()
    {
        var withSha = new FakeAssembly("0.7.0+9c1e2f0a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e", new Version(0, 7, 0, 0));
        var withoutSha = new FakeAssembly("1.2.3-beta.1", new Version(1, 2, 3, 0));

        GameServerVersion.Of(withSha).ShouldBe("0.7.0");
        GameServerVersion.InformationalOf(withSha).ShouldBe("0.7.0+9c1e2f0a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e");
        GameServerVersion.Of(withoutSha).ShouldBe("1.2.3-beta.1");
        GameServerVersion.InformationalOf(withoutSha).ShouldBe("1.2.3-beta.1");
    }

    [Fact]
    public void Without_an_informational_version_the_assembly_version_gives_three_parts_else_unknown()
    {
        GameServerVersion.Of(new FakeAssembly(null, new Version(2, 5, 1, 9))).ShouldBe("2.5.1");
        GameServerVersion.InformationalOf(new FakeAssembly(string.Empty, new Version(2, 5))).ShouldBe("2.5.0");
        GameServerVersion.Of(new FakeAssembly(null, null)).ShouldBe("unknown");
        GameServerVersion.InformationalOf(new FakeAssembly(null, null)).ShouldBe("unknown");
    }

    [Fact]
    public void The_framework_version_is_the_platform_assembly_stamp()
    {
        var platform = typeof(GameServerVersion).Assembly;

        GameServerVersion.FrameworkInformational.ShouldBe(platform.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        GameServerVersion.Framework.ShouldBe(GameServerVersion.FrameworkInformational.Split('+')[0]);
    }

    /// <summary>An assembly with a chosen informational version attribute and assembly version.</summary>
    private sealed class FakeAssembly(string? informational, Version? version) : Assembly
    {
        public override AssemblyName GetName() => new("Fake") { Version = version };

        // Typed arrays, as the runtime returns them: Attribute.GetCustomAttributes casts the result to Attribute[].
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) =>
            informational is not null && attributeType.IsAssignableFrom(typeof(AssemblyInformationalVersionAttribute))
                ? new[] { new AssemblyInformationalVersionAttribute(informational) }
                : (object[])Array.CreateInstance(attributeType, 0);
    }
}
