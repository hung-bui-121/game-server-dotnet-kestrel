using System.Collections;
using System.Text;
using GameServer.LocalRun;

// Host resolver of tools/local/run-local.ps1|.sh (contracts/run-local-cli.md section 2, data-model section 7).
//   describe --host-project <path> --launch-profile <name>   key=value lines on stdout; exit 2 with one message on stderr
const int ExitInvalidInput = 2;
const string Usage = "usage: GameServer.LocalRun describe --host-project <project file or folder> --launch-profile <name>";

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
string? hostProject = null;
string? launchProfile = null;
if (args.Length == 0 || args[0] != "describe")
{
    return Fail(Usage);
}

for (var i = 1; i < args.Length; i++)
{
    if (i + 1 >= args.Length)
    {
        return Fail($"option '{args[i]}' needs a value. {Usage}");
    }

    switch (args[i])
    {
        case "--host-project":
            hostProject = args[++i];
            break;
        case "--launch-profile":
            launchProfile = args[++i];
            break;
        default:
            return Fail($"unknown option '{args[i]}'. {Usage}");
    }
}

if (string.IsNullOrWhiteSpace(hostProject) || string.IsNullOrWhiteSpace(launchProfile))
{
    return Fail(Usage);
}

try
{
    var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
        .Select(entry => KeyValuePair.Create((string)entry.Key, (string?)entry.Value));
    var description = LocalHostDescription.Describe(hostProject, launchProfile, Environment.CurrentDirectory, FrameworkRoot(), environment);
    foreach (var line in description.ToLines())
    {
        Console.Out.WriteLine(line);
    }

    return 0;
}
catch (LocalRunException exception)
{
    return Fail(exception.Message);
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return ExitInvalidInput;
}

// The framework checkout this tool was built in (bin/<configuration>/<tfm> below tools/GameServer.LocalRun).
static string FrameworkRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "GameServer.slnx")))
        {
            return directory.FullName;
        }
    }

    throw new InvalidOperationException("GameServer.slnx not found above " + AppContext.BaseDirectory);
}
