using System.Xml.Linq;
using Shouldly;

namespace GameServer.WireGen.Tests;

/// <summary>Slice 014 T040 (FR-020, FR-022): the tool carries the framework checkout, its Grpc.Tools folder and the pinned versions.</summary>
public sealed class ToolInfoTests
{
    [Fact]
    public void The_baked_metadata_names_the_framework_pins_and_the_restored_protoc()
    {
        var tool = ToolInfo.Current;
        var packages = XDocument.Load(Repo.Path("Directory.Packages.props")).Descendants("PackageVersion")
            .ToDictionary(p => (string)p.Attribute("Include")!, p => (string)p.Attribute("Version")!);
        var declared = XDocument.Load(Repo.Path("Directory.Build.props")).Descendants("GameServerFrameworkVersion").Single().Value.Trim();

        File.Exists(Path.Combine(tool.FrameworkRoot, "GameServer.slnx")).ShouldBeTrue(tool.FrameworkRoot);
        tool.GrpcToolsVersion.ShouldBe(packages["Grpc.Tools"]);
        tool.ProtobufVersion.ShouldBe(packages["Google.Protobuf"]);
        tool.FrameworkVersion.ShouldBe(declared);
        tool.GrpcToolsVersion.ShouldBe("2.84.0");
        tool.ProtobufVersion.ShouldBe("3.36.2");
        tool.FrameworkVersion.ShouldBe("0.7.0");
        File.Exists(Protoc.Locate(tool).Executable).ShouldBeTrue();
        Directory.Exists(Path.Combine(tool.GrpcToolsRoot, "build", "native", "include", "google", "protobuf")).ShouldBeTrue();
    }
}
