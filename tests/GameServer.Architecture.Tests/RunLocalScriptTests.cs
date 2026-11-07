using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Shouldly;

namespace GameServer.Architecture.Tests;

/// <summary>
/// Slice 013 T058 (FR-041, FR-045, SC-005; contracts/run-local-cli.md §6): <c>tools/local/run-local.ps1</c> (Windows
/// PowerShell) and <c>run-local.sh</c> (when bash is found). Always: the exits 2 and 4 that happen before any host build.
/// Opt-in <c>GAMESERVER_SCRIPT_TESTS=1</c> (Windows; the graceful stop uses <c>send-ctrl-c.ps1</c>): the default invocation
/// and the consumer fixture host start, print their banner and stop with exit 0. The default invocation runs the sample
/// host on the owner's local data folder, so it never passes <c>-Reset</c>.
/// </summary>
public sealed class RunLocalScriptTests : IDisposable
{
    public const string Variable = "GAMESERVER_SCRIPT_TESTS";

    private const string Ready = "Press Ctrl+C to stop.";
    private const int SamplePort = 5080;

    private static readonly TimeSpan FastTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

    private static readonly string Scripts = Path.Combine(RepositoryTooling.Root, "tools", "local");
    private static readonly string FixtureHost = Path.Combine(RepositoryTooling.Root, "tests", "ConsumerFixture", "src", "Fixture.Host");
    private static readonly string FixtureLocal = Path.Combine(RepositoryTooling.Root, "tests", "ConsumerFixture", ".local");
    private static readonly string FixtureData = Path.Combine(FixtureLocal, "data");

    /// <summary>Git-ignored folders the hosts write on their local flavor (sample analytics, fixture data/logs/analytics).</summary>
    private static readonly string[] RunFolders = [Path.Combine(RepositoryTooling.Root, "samples", "SampleHost", "analytics"), FixtureLocal];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gs-local-" + Guid.NewGuid().ToString("N"));

    /// <summary>Run folders absent before the test: removed afterwards, so a test run leaves none behind (the fixture smoke test refuses <c>.local</c>).</summary>
    private readonly string[] _absentRunFolders = RunFolders.Where(folder => !Directory.Exists(folder)).ToArray();

    public enum Shell
    {
        PowerShell,
        Bash,
    }

    public static TheoryData<Shell> Shells => [Shell.PowerShell, Shell.Bash];

    public void Dispose()
    {
        foreach (var folder in _absentRunFolders.Append(_root).Where(Directory.Exists))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public async Task Invalid_arguments_and_host_inputs_exit_2_with_the_cause(Shell shell)
    {
        RequireShell(shell);
        var bare = Path.Combine(_root, "bare");
        WriteHost(bare, port: null);

        await AssertFailsAsync(shell, Option(shell, "Bogus", "x"), 2, "unknown argument");
        await AssertFailsAsync(shell, Option(shell, "HostProject", Path.Combine(_root, "missing")), 2, "not found. Fix:");
        await AssertFailsAsync(shell, Option(shell, "LaunchProfile", "nope"), 2, "launch profile 'nope' not found");
        await AssertFailsAsync(shell, Option(shell, "HostProject", bare), 2, "no Properties/launchSettings.json in");
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public async Task A_busy_profile_port_exits_4_before_the_host_is_built(Shell shell)
    {
        RequireShell(shell);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var host = Path.Combine(_root, "busy");
        WriteHost(host, port);

        await AssertFailsAsync(shell, Option(shell, "HostProject", host), 4, $"port {port} is in use");
        Directory.Exists(Path.Combine(host, "obj")).ShouldBeFalse("the port is checked before the host build");
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public async Task The_default_invocation_starts_the_sample_host_with_its_banner_and_stops_gracefully(Shell shell)
    {
        RequireOptIn(shell);
        Assert.SkipUnless(PortIsFree(SamplePort), $"port {SamplePort} is in use: the default invocation needs it.");

        using var run = ScriptRun.Start(shell, []);
        await run.WaitForAsync(Ready, StartTimeout);

        run.Output.ShouldContain("Local game server is ready (flavor Local, data ../_database/dbbase)");
        run.Output.ShouldContain("  Player API   http://localhost:5080/          (routes /api/v1/..., /v1/time)");
        run.Output.ShouldContain("  Health       http://localhost:5080/health/ready");
        run.Output.ShouldContain("  GM UI        http://localhost:5080/gm/v1/ui  (staff admin / dev-only-change-me)");
        run.Output.ShouldContain("  GM console   http://localhost:5173  (cd gm-console; npm ci; npm run dev)");
        run.Output.ShouldContain("  GM OpenAPI   http://localhost:5080/gm/v1/openapi/gm.json");
        (await run.StopAsync()).ShouldBe(0, run.Output);
    }

    /// <summary>
    /// Slice 018 review T068 m6: the console's local config.json calls port 5080, so the sample host on another port prints no
    /// console line (it would point at a console that does not call this host).
    /// </summary>
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task The_sample_host_on_another_port_prints_no_console_line(Shell shell)
    {
        RequireOptIn(shell);
        var port = FreePort();

        using var run = ScriptRun.Start(shell, Option(shell, "Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await run.WaitForAsync(Ready, StartTimeout);

        run.Output.ShouldContain($"  GM UI        http://localhost:{port}/gm/v1/ui  (staff admin / dev-only-change-me)");
        run.Output.ShouldNotContain("GM console");
        (await run.StopAsync()).ShouldBe(0, run.Output);
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public async Task The_fixture_host_runs_under_its_name_logs_requests_on_the_console_and_resets_its_data(Shell shell)
    {
        RequireOptIn(shell);

        // Never the fixture profile's port 5180: a game's own host runs there on the owner's machine (even probing it could
        // collide with a host starting meanwhile). The profile-port rule is covered by LocalRunDescribeTests.
        var port = FreePort();
        string[] arguments =
        [
            .. Option(shell, "HostProject", FixtureHost),
            .. Option(shell, "Name", "Fixture game"),
            .. Option(shell, "Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ];

        using (var run = ScriptRun.Start(shell, arguments))
        {
            await run.WaitForAsync(Ready, StartTimeout);
            run.Output.ShouldContain("Fixture game is ready (flavor Local, data " + Slashed(FixtureData) + ")");
            run.Output.ShouldContain($"  Base URL     http://localhost:{port}/");
            run.Output.ShouldContain("  Project      " + Slashed(Path.Combine(FixtureHost, "Fixture.Host.csproj")) + " (profile local)");
            run.Output.ShouldNotContain("GM UI");

            using var client = new HttpClient();
            (await client.GetAsync(new Uri($"http://127.0.0.1:{port}/v1/time"), TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
            await run.WaitForAsync("GET /v1/time", StopTimeout);
            (await run.StopAsync()).ShouldBe(0, run.Output);
        }

        var marker = Path.Combine(FixtureData, "reset-marker.txt");
        await File.WriteAllTextAsync(marker, "deleted by -Reset", TestContext.Current.CancellationToken);
        using (var reset = ScriptRun.Start(shell, [.. arguments, Flag(shell, "Reset")]))
        {
            await reset.WaitForAsync(Ready, StartTimeout);
            reset.Output.ShouldContain(", started empty)");
            File.Exists(marker).ShouldBeFalse("-Reset empties the fixture's data folder");
            (await reset.StopAsync()).ShouldBe(0, reset.Output);
        }
    }

    /// <summary>Review m2: "dotnet run" gets the project file, so a host folder with a second project file still runs the named one.</summary>
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task A_host_folder_with_two_project_files_runs_the_named_project(Shell shell)
    {
        RequireOptIn(shell);
        var port = FreePort();
        var host = Path.Combine(_root, "two-projects");
        WriteHost(host, port);
        File.WriteAllText(
            Path.Combine(host, "Probe.Host.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>""");
        File.WriteAllText(Path.Combine(host, "Program.cs"), """var app = WebApplication.Create(args); app.MapGet("/health/ready", () => "ready"); app.Run();""");
        File.WriteAllText(Path.Combine(host, "Probe.Tool.csproj"), """<Project Sdk="Microsoft.NET.Sdk" />""");

        using var run = ScriptRun.Start(shell, Option(shell, "HostProject", Path.Combine(host, "Probe.Host.csproj")));
        await run.WaitForAsync(Ready, StartTimeout);

        run.Output.ShouldContain("Probe.Host is ready (flavor Local");
        run.Output.ShouldContain($"  Base URL     http://localhost:{port}/");
        (await run.StopAsync()).ShouldBe(0, run.Output);
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public async Task A_fixture_setting_that_fails_startup_exits_6_with_the_log_tail(Shell shell)
    {
        RequireOptIn(shell);
        string[] arguments = [.. Option(shell, "HostProject", FixtureHost), .. Option(shell, "Port", FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture))];

        // A startup validation failure ends before the file log is written: the probe is the newest file of the log folder.
        var logs = Directory.CreateDirectory(Path.Combine(FixtureLocal, "logs")).FullName;
        var probe = Path.Combine(logs, $"run-local-probe-{Guid.NewGuid():N}.log");
        var probeLine = "probe line " + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(probe, probeLine + Environment.NewLine, TestContext.Current.CancellationToken);
        try
        {
            using var run = ScriptRun.Start(shell, arguments, new Dictionary<string, string?> { ["GameServer__Analytics__TimeZone"] = "Not/AZone" });
            var exitCode = await run.WaitForExitAsync(StartTimeout);

            exitCode.ShouldBe(6, run.Output);
            run.Output.ShouldContain("GameServer:Analytics:TimeZone 'Not/AZone' is not a known time zone", Case.Sensitive, "the host's own message");
            run.Output.ShouldContain("before it was ready");
            run.Output.ShouldContain("Last 20 lines of ");
            run.Output.ShouldContain("  " + probeLine);
        }
        finally
        {
            File.Delete(probe);
        }
    }

    private static void RequireShell(Shell shell)
    {
        Assert.SkipWhen(RepositoryTooling.Dotnet() is null, "No dotnet executable: the scripts need the SDK.");
        Assert.SkipWhen(ShellCommand(shell) is null, shell == Shell.Bash ? "bash not found (Git Bash on Windows)." : "Windows PowerShell runs on Windows only.");
    }

    private static void RequireOptIn(Shell shell)
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(Variable) == "1",
            $"{Variable} not set to 1: the run-local start/stop checks build and start real hosts (opt-in, SC-005).");
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The graceful stop uses tools/local/send-ctrl-c.ps1 (Windows); macOS/Linux stay a manual check.");
        RequireShell(shell);
    }

    /// <summary>Windows PowerShell; bash = Git Bash next to git on Windows (System32\bash.exe is the WSL launcher), else PATH.</summary>
    private static string? ShellCommand(Shell shell)
    {
        if (shell == Shell.PowerShell)
        {
            return OperatingSystem.IsWindows() ? RepositoryTooling.FindOnPath("powershell.exe") : null;
        }

        if (!OperatingSystem.IsWindows())
        {
            return RepositoryTooling.FindOnPath("bash");
        }

        // git.exe lives in Git\cmd or Git\mingw64\bin depending on the PATH entry; bash.exe in Git\bin.
        var candidates = new List<string>();
        for (var folder = Path.GetDirectoryName(RepositoryTooling.FindOnPath("git.exe")); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            candidates.Add(Path.Combine(folder, "bin", "bash.exe"));
        }

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string[] ShellArguments(Shell shell, IEnumerable<string> arguments) => shell == Shell.PowerShell
        ? ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Scripts, "run-local.ps1"), .. arguments]
        : [Path.Combine(Scripts, "run-local.sh").Replace('\\', '/'), .. arguments];

    /// <summary><c>-HostProject x</c> for PowerShell, <c>--host-project x</c> for bash.</summary>
    private static string[] Option(Shell shell, string name, string value) => shell == Shell.PowerShell
        ? ["-" + name, value]
        : ["--" + Kebab(name), value.Replace('\\', '/')];

    private static string Flag(Shell shell, string name) => shell == Shell.PowerShell ? "-" + name : "--" + Kebab(name);

    private static string Kebab(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i == 0 ? string.Empty : "-") + char.ToLowerInvariant(c) : c.ToString()));

    private static async Task AssertFailsAsync(Shell shell, string[] arguments, int exitCode, string message)
    {
        var result = await RepositoryTooling.RunAsync(ShellCommand(shell)!, ShellArguments(shell, arguments), RepositoryTooling.Root, FastTimeout);
        result.ExitCode.ShouldBe(exitCode, $"{shell} {string.Join(' ', arguments)}: {result.All}");
        result.Error.ShouldContain("run-local: ");
        result.Error.ShouldContain(message, Case.Sensitive, $"{shell} {string.Join(' ', arguments)}");
    }

    private static bool PortIsFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string Slashed(string path) => path.Replace('\\', '/');

    /// <summary>A host layout with a <c>global.json</c> like the framework's; <paramref name="port"/> null = no launch settings.</summary>
    private static void WriteHost(string folder, int? port)
    {
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(RepositoryTooling.Root, "global.json"), Path.Combine(folder, "global.json"));
        File.WriteAllText(Path.Combine(folder, "Probe.Host.csproj"), """<Project Sdk="Microsoft.NET.Sdk.Web" />""");
        if (port is not null)
        {
            Directory.CreateDirectory(Path.Combine(folder, "Properties"));
            File.WriteAllText(
                Path.Combine(folder, "Properties", "launchSettings.json"),
                $$"""{ "profiles": { "local": { "applicationUrl": "http://localhost:{{port}}", "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Local" } } } }""");
        }
    }

    /// <summary>One script run in its own hidden console (so a console Ctrl+C reaches only the script and its host).</summary>
    private sealed class ScriptRun : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _output = new();

        private ScriptRun(Process process)
        {
            _process = process;
        }

        public string Output
        {
            get
            {
                lock (_output)
                {
                    return _output.ToString();
                }
            }
        }

        public static ScriptRun Start(Shell shell, IEnumerable<string> arguments, IReadOnlyDictionary<string, string?>? environment = null)
        {
            var start = new ProcessStartInfo(ShellCommand(shell)!)
            {
                WorkingDirectory = RepositoryTooling.Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in ShellArguments(shell, arguments))
            {
                start.ArgumentList.Add(argument);
            }

            foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                start.Environment.Remove(name);
            }

            foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
            {
                start.Environment[name] = value;
            }

            var run = new ScriptRun(Process.Start(start) ?? throw new InvalidOperationException("the script did not start."));
            run._process.OutputDataReceived += (_, e) => run.Append(e.Data);
            run._process.ErrorDataReceived += (_, e) => run.Append(e.Data);
            run._process.BeginOutputReadLine();
            run._process.BeginErrorReadLine();
            return run;
        }

        public async Task WaitForAsync(string text, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!Output.Contains(text, StringComparison.Ordinal))
            {
                if (_process.HasExited)
                {
                    throw new ShouldAssertException($"the script exited ({_process.ExitCode}) before '{text}': {Output}");
                }

                (DateTime.UtcNow < deadline).ShouldBeTrue($"'{text}' not seen within {timeout}: {Output}");
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }

        public async Task<int> WaitForExitAsync(TimeSpan timeout)
        {
            using var deadline = new CancellationTokenSource(timeout);
            await _process.WaitForExitAsync(deadline.Token);
            _process.WaitForExit(); // drains the redirected streams
            return _process.ExitCode;
        }

        /// <summary>Ctrl+C on the script's console (graceful host stop), then the script's exit code.</summary>
        public async Task<int> StopAsync()
        {
            var signal = await RepositoryTooling.RunAsync(
                RepositoryTooling.FindOnPath("powershell.exe")!,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Scripts, "send-ctrl-c.ps1"),
                    "-ProcessId", _process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), "-Signal", "C"],
                RepositoryTooling.Root,
                StopTimeout);
            signal.ExitCode.ShouldBe(0, signal.All);
            return await WaitForExitAsync(StopTimeout);
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }

            _process.Dispose();
        }

        private void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_output)
            {
                _output.AppendLine(line);
            }
        }
    }
}
