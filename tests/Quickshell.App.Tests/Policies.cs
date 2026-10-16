using System.IO;
using System.Text.Json.Nodes;

namespace Quickshell.App.Tests;

/// <summary>Agent policies a test writes for itself (QS336).</summary>
internal static class Policies
{
    /// <summary>
    /// A policy file letting everything run on every session for the next hour: a break-glass grant, the
    /// person's own widening, which is what a test exercising mutations stands on until approval (QS337)
    /// can answer an ask.
    /// </summary>
    public static string BreakGlass()
    {
        string file = Path.Combine(Path.GetTempPath(), $"qs-policy-{Guid.NewGuid():N}.json");

        File.WriteAllText(file, new JsonObject
        {
            ["schema"] = 1,
            ["default"] = "deny",
            ["grants"] = new JsonArray(new JsonObject
            {
                ["sessions"] = new JsonArray("*"),
                ["level"] = "break-glass",
                ["until"] = DateTimeOffset.Now.AddHours(1).ToString("O"),
                ["why"] = "a test exercising mutations",
            }),
        }.ToJsonString());

        return file;
    }
}
