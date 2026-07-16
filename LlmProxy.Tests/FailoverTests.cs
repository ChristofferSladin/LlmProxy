using Xunit;

namespace LlmProxy.Tests;

/// <summary>
/// Guards the existing streaming-peek / 200-err / failover path offline. The acceptance check filters
/// on <c>FailoverTests</c>.
/// </summary>
public sealed class FailoverTests
{
    private const string ExhaustedBody =
        "{\"error\":{\"message\":\"ResourceExhausted: Worker local total request limit reached (48/48)\"}}";

    private static string Completion(string model, string content) =>
        $"{{\"id\":\"cmpl-1\",\"object\":\"chat.completion\",\"model\":\"{model}\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"" + content + "\"},\"finish_reason\":\"stop\"}]}";

    private const string Request =
        "{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

    [Fact]
    public async Task First_model_200_error_body_fails_over_to_second_model()
    {
        // One attempt per model, so the recorded chain is exactly [deepseek, llama] rather than
        // [deepseek, deepseek(retry), llama] — the 200-err peek/classify/failover path is still exercised.
        var host = TestHost.Create(new[] { "deepseek", "llama" }, configureOptions: o => o.MaxAttemptsPerModel = 1);
        host.Upstream
            .Enqueue(modelMatch: "deepseek", status: 200, body: ExhaustedBody)
            .Enqueue(modelMatch: "llama", status: 200, body: Completion("llama", "hello from llama"));

        var result = await host.ForwardAsync(Request);

        Assert.Equal(200, result.Status);
        Assert.Contains("hello from llama", result.Body);

        // Failover happened: deepseek was tried first, then llama.
        Assert.Equal(new[] { "deepseek", "llama" }, host.Upstream.TriedModels);
    }

    [Fact]
    public async Task Plain_good_200_on_first_model_is_returned_as_is()
    {
        var host = TestHost.Create(new[] { "deepseek", "llama" });
        host.Upstream
            .Enqueue(modelMatch: "deepseek", status: 200, body: Completion("deepseek", "hello from deepseek"));

        var result = await host.ForwardAsync(Request);

        Assert.Equal(200, result.Status);
        Assert.Contains("hello from deepseek", result.Body);

        // No false-positive error classification: only the first model was ever called.
        Assert.Equal(new[] { "deepseek" }, host.Upstream.TriedModels);
    }
}
