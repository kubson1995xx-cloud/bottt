namespace Apex.Naval.Replay;

public static class Program
{
    public static int Main(string[] args) => new ReplayApplication().Run(args, Console.Out, Console.Error);
}
