using LinuxCleaner;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancellation.Cancel();
};
return Cli.Run(args, Console.In, Console.Out, Console.Error, Console.IsInputRedirected, cancellation.Token);
