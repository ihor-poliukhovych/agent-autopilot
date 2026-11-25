// TEMPORARY: signatures agreed between parallel work streams. Each owner moves its
// type into its own file, implements it, and deletes it from here.
using Autopilot.Core.Config;
using Autopilot.Core.Gate;
using Autopilot.Core.Policy;

namespace Autopilot.Core.Paths
{
    public static class GlobMatcher
    {
        /// <summary>Turns a path into a project-relative form with forward slashes and no leading "./".</summary>
        public static string Normalize(string relativePath) => throw new NotImplementedException();

        /// <summary>True when the project-relative path matches any of the glob patterns (case-insensitive).</summary>
        public static bool IsMatch(string relativePath, IEnumerable<string> patterns) => throw new NotImplementedException();
    }
}

namespace Autopilot.Core.Gate
{
    public static class MergeGate
    {
        /// <summary>Paths no agent may change, whatever the config says.</summary>
        public static IReadOnlyList<string> ProtectedPaths { get; } = [".autopilot/**"];

        public static GateDecision Evaluate(GateInput input, AutopilotConfig config) => throw new NotImplementedException();
    }
}

namespace Autopilot.Core.Policy
{
    public static class ToolPolicyEvaluator
    {
        /// <summary>Decides one tool call. callNumber is 1 for the first call of the run.</summary>
        public static PolicyDecision Evaluate(ToolCall call, ToolPolicy policy, int callNumber) => throw new NotImplementedException();
    }

    public static class ToolCallCounter
    {
        /// <summary>Atomically increments the counter in the file (creating it at 0) and returns the new value.</summary>
        public static int Increment(string counterFile) => throw new NotImplementedException();
    }
}
