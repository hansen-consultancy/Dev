// 'Dev' tool can be used to do some development tasks based on the current directory.

using System.Diagnostics;

var path = Environment.CurrentDirectory;

// Check if we have a .sln file in the current directory. And if so, open it in Visual Studio.
var slnFile = Directory.GetFiles(path, "*.sln").FirstOrDefault();
if (slnFile != null)
{
    Console.WriteLine($"Opening {slnFile} in Visual Studio...");
    Process.Start("devenv.exe", slnFile);
    return;
}

// Check if we have a .csproj file in the current directory. And if so, open it in Visual Studio Code.
var csprojFile = Directory.GetFiles(path, "*.csproj").FirstOrDefault();
if (csprojFile != null)
{
    Console.WriteLine($"Opening {csprojFile} in Visual Studio Code...");
    Process.Start("code", csprojFile);
    return;
}

// Nothing to do.
Console.WriteLine("No .sln or .csproj file found in the current directory.");