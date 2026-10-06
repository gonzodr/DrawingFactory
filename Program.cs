internal static class Program
{
    [STAThread]
    private static int Main(string[] args) =>
        (int)new DrawingFactoryApplication(Console.Out, Console.Error).Run(args);
}