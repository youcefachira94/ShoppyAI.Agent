using ShoppyAI.Core.Ollama;

namespace ShoppyAI.Tests;

public sealed class OllamaEndpointTests
{
    [Fact]
    public void Defaults_to_localhost()
    {
        var options = new OllamaOptions();

        Assert.Equal("http://localhost:11434", options.BaseUrl);
        Assert.False(options.AllowRemoteEndpoint);
    }

    [Fact]
    public void Remote_endpoint_opt_in_persists_in_user_settings()
    {
        var directory = TestData.NewTempDirectory();
        try
        {
            var file = Path.Combine(directory, "settings.json");
            var settings = new ShoppyAI.Core.Settings.SettingsStore(file);
            settings.Settings.Ollama.BaseUrl = "https://ollama.example.com";
            settings.Settings.Ollama.AllowRemoteEndpoint = true;
            settings.Save();

            var reloaded = new ShoppyAI.Core.Settings.SettingsStore(file);

            Assert.True(reloaded.Settings.Ollama.AllowRemoteEndpoint);
            Assert.Equal("https://ollama.example.com", reloaded.Settings.Ollama.BaseUrl);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://ollama.example.com")]
    [InlineData("http://192.0.2.7:11434")]
    public void Refuses_remote_endpoints_without_explicit_opt_in(string endpoint)
    {
        var options = new OllamaOptions { BaseUrl = endpoint };

        Assert.Throws<ArgumentException>(() => new OllamaChatClient(new HttpClient(new FakeOllamaHandler(_ => FakeOllamaHandler.Stream())), options));
    }

    [Fact]
    public void Refuses_remote_http_even_with_opt_in()
    {
        var options = new OllamaOptions { BaseUrl = "http://ollama.example.com", AllowRemoteEndpoint = true };

        Assert.Throws<ArgumentException>(() => new OllamaChatClient(new HttpClient(new FakeOllamaHandler(_ => FakeOllamaHandler.Stream())), options));
    }

    [Fact]
    public async Task Allows_explicit_remote_https_endpoint()
    {
        var options = new OllamaOptions { BaseUrl = "https://ollama.example.com:9443/ollama", AllowRemoteEndpoint = true };
        var handler = new FakeOllamaHandler(_ => FakeOllamaHandler.Stream());

        var client = new OllamaChatClient(new HttpClient(handler), options);
        await client.CompleteAsync([ShoppyAI.Core.Conversations.ChatMessage.User("hello")]);

        Assert.Equal("https://ollama.example.com:9443/ollama/api/chat", Assert.Single(handler.Requests).Uri.ToString());
    }

    [Theory]
    [InlineData("file:///tmp/ollama")]
    [InlineData("not a url")]
    [InlineData("https://user:password@ollama.example.com")]
    public void Rejects_invalid_or_credential_bearing_urls(string endpoint)
    {
        var options = new OllamaOptions { BaseUrl = endpoint, AllowRemoteEndpoint = true };

        Assert.Throws<ArgumentException>(() => new OllamaChatClient(new HttpClient(new FakeOllamaHandler(_ => FakeOllamaHandler.Stream())), options));
    }
}
