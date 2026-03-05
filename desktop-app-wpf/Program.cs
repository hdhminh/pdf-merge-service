using System;
using PdfStampNgrokDesktop.Services;
using Velopack;

namespace PdfStampNgrokDesktop;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Any(static x => string.Equals(x, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            var code = SmokeSelfTestRunner.RunAsync().GetAwaiter().GetResult();
            Environment.ExitCode = code;
            return;
        }

        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
