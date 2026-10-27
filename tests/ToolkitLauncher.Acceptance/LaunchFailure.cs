using System.IO;
using ToolkitLauncher.Core;

namespace ToolkitLauncher.Acceptance;

internal static class LaunchFailure
{
    internal static int Capture(string evidence, Func<string> stage, Action<string> log, Func<int> action)
    {
        try { return action(); }
        catch (Exception error)
        {
            Save(evidence, stage(), error);
            log("Launch failed at " + stage() + ": " + error);
            throw;
        }
    }

    internal static void Save(string evidence, string stage, Exception error)
    {
        try
        {
            var path = Path.Combine(evidence, "launch-error.json");
            OwnedPaths.Write(path, new
            {
                FailedUtc = DateTimeOffset.UtcNow, Stage = stage, ErrorType = error.GetType().FullName,
                error.Message, FullException = error.ToString(), EvidenceDirectory = evidence,
                Harness = "Toolkit.QA 1.4.0-qa.12", ProductionSourceCommit = CandidateManifest.SourceCommit
            });
            Console.Error.WriteLine("Durable QA launch error: " + path);
        }
        catch (Exception saveError) { Console.Error.WriteLine("Could not save QA launch error: " + saveError + "\nOriginal failure: " + error); }
    }
}
