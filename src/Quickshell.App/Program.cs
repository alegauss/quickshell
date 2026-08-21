using System.Reflection;

// The skeleton's entry point. It exists so that cold start is a number somebody can
// measure before the first feature is written, not so that it does anything yet.
string version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "0.0.0";

Console.WriteLine($"quickshell {version}");
Console.WriteLine("No session support yet: this build is the skeleton the roadmap measures against.");
return 0;
