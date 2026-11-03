namespace GameServer.WireGen;

/// <summary>Exit codes of the tool (contracts/wiregen-cli.md §3); changing one is a breaking change (PCR-01).</summary>
internal static class WireGenExitCodes
{
    public const int Ok = 0;

    /// <summary>Input, configuration or contract error; nothing written.</summary>
    public const int InvalidInput = 1;

    /// <summary><c>verify</c> only: one or more output files differ or are missing.</summary>
    public const int OutOfDate = 2;
}

/// <summary>The parsed command line (contracts/wiregen-cli.md §2).</summary>
internal sealed record WireGenOptions(bool Verify, string Config, string? OutputRoot, string? Catalog, string Configuration, bool NoBuild, string? Dotnet);

/// <summary>
/// The <c>generate</c> / <c>verify</c> verbs in the step order of contracts/wiregen-cli.md §4: everything that can fail runs before
/// the first write (config, paths, catalog, versions, protoc descriptor pass, clashes, selection, rendering, protoc C# pass).
/// </summary>
internal static class WireGenCli
{
    public const string Usage =
        """
        usage: dotnet run --project <framework>/tools/GameServer.WireGen -c Release -- <verb> --config <wiregen.json> [options]

        verbs:
          generate   write the generated files that differ from what is on disk
          verify     write nothing; exit 2 when any output file differs or is missing

        options:
          --config <file>          required; wiregen.json (format gameserver.wiregen/1)
          --output-root <dir>      overrides output.root (e.g. the Unity client checkout)
          --catalog <file>         use this export instead of config.catalog (no host run)
          --configuration <cfg>    configuration used to build/run the host (default Release)
          --no-build               pass --no-build to the host run (CI after a solution build)
          --dotnet <path>          dotnet executable for the host run (default: DOTNET_HOST_PATH, the running dotnet, DOTNET_ROOT, PATH)

        exit codes: 0 done / up to date, 1 input or contract error (nothing written), 2 verify found differences
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, ToolInfo? tool = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 1 && args[0] is "--help" or "-h" or "help")
        {
            await output.WriteLineAsync(Usage);
            return WireGenExitCodes.Ok;
        }

        WireGenOptions options;
        try
        {
            options = Parse(args);
        }
        catch (WireGenException ex)
        {
            await error.WriteLineAsync(ex.Message);
            await error.WriteLineAsync(Usage);
            return WireGenExitCodes.InvalidInput;
        }

        try
        {
            return await RunAsync(options, tool ?? ToolInfo.Current, output, error);
        }
        catch (WireGenException ex)
        {
            foreach (var line in ex.Lines)
            {
                await error.WriteLineAsync(line);
            }

            return WireGenExitCodes.InvalidInput;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"[io] {ex.Message}");
            return WireGenExitCodes.InvalidInput;
        }
#pragma warning disable CA1031 // Exit codes are a contract (§3): a bug of the tool is exit 1 with one line, never an unhandled crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await error.WriteLineAsync($"[internal] {ex.GetType().Name}: {ex.Message} Fix: report this generator failure with the command line used.");
            return WireGenExitCodes.InvalidInput;
        }
    }

    internal static WireGenOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("generate" or "verify"))
        {
            throw new WireGenException(args.Length == 0 ? "error: no verb." : $"error: unknown verb '{args[0]}'.");
        }

        string? config = null, outputRoot = null, catalog = null, dotnet = null;
        var configuration = "Release";
        var noBuild = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--no-build")
            {
                noBuild = true;
                continue;
            }

            if (args[i] is not ("--config" or "--output-root" or "--catalog" or "--configuration" or "--dotnet"))
            {
                throw new WireGenException($"error: unknown option '{args[i]}'.");
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new WireGenException($"error: option '{args[i]}' needs a value.");
            }

            var value = args[++i];
            switch (args[i - 1])
            {
                case "--config":
                    config = value;
                    break;
                case "--output-root":
                    outputRoot = value;
                    break;
                case "--catalog":
                    catalog = value;
                    break;
                case "--configuration":
                    configuration = value;
                    break;
                default:
                    dotnet = value;
                    break;
            }
        }

        return config is null
            ? throw new WireGenException("error: --config <wiregen.json> is required.")
            : new WireGenOptions(args[0] == "verify", config, outputRoot, catalog, configuration, noBuild, dotnet);
    }

    private static async Task<int> RunAsync(WireGenOptions options, ToolInfo tool, TextWriter output, TextWriter error)
    {
        // Steps 1-3: config, output root / marker / directory, protos (nothing created on failure).
        var config = WireGenConfig.Load(options.Config, options.OutputRoot, options.Catalog);
        var temp = Directory.CreateTempSubdirectory("wiregen-");
        try
        {
            // Steps 4-5: catalog and versions; a committed export needs no dotnet (a given --dotnet is still checked).
            WireContract contract;
            List<string> errors;
            if (config.CatalogFile is { } catalogFile)
            {
                if (options.Dotnet is not null)
                {
                    DotnetHost.Resolve(options.Dotnet);
                }

                contract = CatalogSource.Read(catalogFile);
                errors = [.. VersionGuard.CheckExport(contract, tool)];
            }
            else
            {
                var dotnet = DotnetHost.Resolve(options.Dotnet);
                contract = await CatalogSource.ExportAsync(config, dotnet, options.Configuration, options.NoBuild, temp.FullName);
                errors = [.. VersionGuard.CheckExport(contract, tool)];
                errors.AddRange(await VersionGuard.CheckHostPackagesAsync(dotnet, config.HostProject!, tool, note => error.WriteLine(note)));
            }

            if (errors.Count > 0)
            {
                throw new WireGenException(errors);
            }

            // Steps 6-7: descriptor pass over consumer + framework protos, C# name clashes.
            var protoc = Protoc.Locate(tool);
            var consumer = config.Protos.SelectMany(set => set.Files.Select(file => new ProtoFile(set.Root, file))).ToArray();
            var framework = protoc.FrameworkProtos();
            var roots = protoc.ImportRoots(config.Protos.Select(set => set.Root).Concat(config.ImportRoots));
            var descriptors = await protoc.DescribeAsync(roots, consumer.Concat(framework), temp.FullName);
            var model = ProtoModel.Build(descriptors, consumer.Select(p => p.Relative), framework.Select(p => p.Relative));
            if (model.Clashes() is { Count: > 0 } clashes)
            {
                throw new WireGenException(clashes);
            }

            // Step 8: selection and the rules of the generated names, templates and section messages.
            var warnings = new List<string>();
            var routes = RouteRenderer.Render(Selection.Routes(contract, config.Origin), config.Namespace, config.RoutesClass, config.Origin, tool.FrameworkVersion);
            var sections = SectionRenderer.Render(
                Selection.Sections(contract, config.Origin), model, config.Namespace, config.SectionsClass, config.Origin, tool.FrameworkVersion, warnings);

            // Step 9: the C# pass, one protoc run per consumer proto.
            var generated = new List<GeneratedFile>();
            foreach (var proto in consumer)
            {
                generated.Add(await protoc.GenerateCSharpAsync(roots, proto, config.Layout, temp.FullName));
            }

            generated.Add(new GeneratedFile(config.RoutesClass + ".cs", routes));
            generated.Add(new GeneratedFile(config.SectionsClass + ".cs", sections));
            ThrowOnSamePath(generated);

            // Steps 10-11: normalize, then write the changed files or list the differences.
            var files = generated.Select(f => OutputWriter.Normalize(f.RelativePath, f.Text, config.LineEndings)).ToArray();
            foreach (var warning in warnings)
            {
                await error.WriteLineAsync(warning);
            }

            return options.Verify ? await VerifyAsync(config, files, output) : await GenerateAsync(config, files, output);
        }
        finally
        {
            DeleteTemp(temp, error);
        }
    }

    /// <summary>
    /// The temp folder (export, descriptor sets) goes away after every run; a delete that fails (a scanner holding a file) leaves a
    /// note and never changes the exit code or hides the error that ended the run.
    /// </summary>
    internal static void DeleteTemp(DirectoryInfo temp, TextWriter error)
    {
        try
        {
            temp.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"note: the temp folder {temp.FullName} could not be deleted ({ex.Message}); delete it by hand.");
        }
    }

    private static async Task<int> GenerateAsync(WireGenConfig config, OutputFile[] files, TextWriter output)
    {
        var report = OutputWriter.Generate(config.OutputRoot, config.OutputDirectory, files);
        foreach (var path in report.Written)
        {
            await output.WriteLineAsync($"wrote {path}");
        }

        foreach (var path in report.Created)
        {
            await output.WriteLineAsync($"new file {path}: Unity creates {path}.meta on import; commit it");
        }

        await output.WriteLineAsync($"generate: {report.Written.Count} written, {report.Unchanged} unchanged");
        return WireGenExitCodes.Ok;
    }

    private static async Task<int> VerifyAsync(WireGenConfig config, OutputFile[] files, TextWriter output)
    {
        var report = OutputWriter.Verify(config.OutputRoot, config.OutputDirectory, files);
        foreach (var line in report.Differences)
        {
            await output.WriteLineAsync(line);
        }

        if (report.Differences.Count > 0)
        {
            await output.WriteLineAsync($"verify: {report.Differences.Count} files differ from what generate would write. Fix: run generate and commit the result.");
            return WireGenExitCodes.OutOfDate;
        }

        await output.WriteLineAsync($"verify: {report.Unchanged} files up to date");
        return WireGenExitCodes.Ok;
    }

    /// <summary>A flat layout puts every file in one folder: two outputs with one name would overwrite each other.</summary>
    private static void ThrowOnSamePath(IEnumerable<GeneratedFile> files)
    {
        var errors = files
            .GroupBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"[output] {g.Count()} generated files are named {g.Key} in output.directory. Fix: rename a proto, a class in wiregen.json, " +
                "or use \"layout\": \"by-root\".")
            .ToArray();
        if (errors.Length > 0)
        {
            throw new WireGenException(errors);
        }
    }
}
