using CommandLine;
using ora2mqtt;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    cts.Cancel();
    e.Cancel = true;
};

try
{
    return await Parser.Default.ParseArguments<ConfigureCommand, RunCommand, LoginCommand, ProbeCommand>(args)
        .MapResult(
            (ConfigureCommand x) => x.Run(cts.Token),
            (RunCommand x) => x.Run(cts.Token),
            (LoginCommand x) => x.Run(cts.Token),
            (ProbeCommand x) => x.Run(cts.Token),
            _ => Task.FromResult(1));
}
catch (Exception ex)
{
    Console.WriteLine(ex);
    return -1;
}