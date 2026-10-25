using System;
using System.Reflection;

[assembly: AssemblyTitle("应用清单专用测试应用")]
[assembly: AssemblyProduct("App Inventory Test Application")]
[assembly: AssemblyVersion("1.0.0.0")]

internal static class Program
{
    private static int Main()
    {
        Console.WriteLine("这是应用清单创建的专用测试应用。");
        return 0;
    }
}
