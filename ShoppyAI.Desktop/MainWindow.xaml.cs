
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace ShoppyAI.Desktop;

public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri("http://localhost:11434/"),
        Timeout = TimeSpan.FromMinutes(5)
    };

    private bool _isGenerating;

    public MainWindow()
    {
        InitializeComponent();

        AddMessage(
            "ShoppyAI",
            "Hello! Qwen2.5-Coder 14B is ready to help you.");
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_isGenerating)
            return;

        var text = MessageBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(text))
            return;

        AddMessage("You", text);
        MessageBox.Clear();

        _isGenerating = true;

        try
        {
            AddMessage("ShoppyAI", "Thinking...");

            var request = new
            {
                model = "qwen2.5-coder:14b",
                prompt = text,
                stream = false
            };

            using var response = await _httpClient.PostAsJsonAsync(
                "api/generate",
                request
            );

            response.EnsureSuccessStatusCode();

            var result = await response.Content
                .ReadFromJsonAsync<OllamaResponse>();

            AddMessage(
                "ShoppyAI",
                result?.Response ?? "No response received."
            );
        }
        catch (Exception ex)
        {
            AddMessage(
                "ShoppyAI",
                $"Ollama connection error: {ex.Message}"
            );
        }
        finally
        {
            _isGenerating = false;
        }
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (_isGenerating)
            return;

        MessagesPanel.Children.Clear();
        AddMessage("ShoppyAI", "New conversation started.");
    }

    private void ProjectFiles_Click(object sender, RoutedEventArgs e)
    {
        AddMessage(
            "ShoppyAI",
            "Project file scanning will be connected next."
        );
    }

    private void GitStatus_Click(object sender, RoutedEventArgs e)
    {
        AddMessage(
            "ShoppyAI",
            "Git status will be connected next."
        );
    }

    private void AddMessage(string author, string message)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 18)
        };

        panel.Children.Add(new TextBlock
        {
            Text = author,
            Foreground = author == "You"
                ? System.Windows.Media.Brushes.LightBlue
                : System.Windows.Media.Brushes.LightGreen,
            FontWeight = FontWeights.Bold,
            FontSize = 14
        });

        panel.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0)
        });

        MessagesPanel.Children.Add(panel);
    }

    protected override void OnClosed(EventArgs e)
    {
        _httpClient.Dispose();
        base.OnClosed(e);
    }

    private sealed record OllamaResponse(
        [property: JsonPropertyName("response")] string Response
    );
}
