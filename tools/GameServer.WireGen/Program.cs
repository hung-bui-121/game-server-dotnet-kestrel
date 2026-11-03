using System.Text;
using GameServer.WireGen;

// Consumer Unity code generation (slice 014 FG-021): contracts/wiregen-cli.md.
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
return await WireGenCli.RunAsync(args, Console.Out, Console.Error);
