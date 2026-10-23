using ShoppyAI.Core.Language;

namespace ShoppyAI.Tests;

public sealed class GitSafetyPromptTests
{
    [Fact]
    public void Git_examples_do_not_recommend_global_staging_and_describe_reviewed_flow()
    {
        var assistantExamples = TestData.Resources.Examples
            .SelectMany(example => example.Turns)
            .Where(turn => turn.Role == "assistant")
            .Select(turn => turn.Content)
            .ToArray();

        Assert.DoesNotContain(assistantExamples, text => text.Contains("git add .", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(assistantExamples, text => text.Contains("git stage", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(assistantExamples, text => text.Contains("confirmation séparée", StringComparison.OrdinalIgnoreCase));
    }
}
