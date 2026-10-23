using System.Text;
using System.Windows;
using System.Windows.Controls;
using ShoppyAI.Core.Chat;
using ShoppyAI.Core.Language;
using ShoppyAI.Core.Ollama;
using ShoppyAI.Core.Settings;

namespace ShoppyAI.Desktop;

public partial class MainWindow : Window
{
    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(120);

    private readonly SettingsStore _settings = new();
    private readonly CorrectionStore _corrections = new(ShoppyPaths.CorrectionsFile);
    private readonly LanguageResources _language = LanguageResources.Load(ShoppyPaths.LanguageDirectory);
    private readonly ChatSession _session;
    private readonly LanguageCommands _commands;
    private CancellationTokenSource? _reply;

    public MainWindow()
    {
        InitializeComponent();

        var client = new OllamaChatClient(OllamaChatClient.CreateHttpClient(), _settings.EffectiveOllamaOptions());
        _session = new ChatSession(client, _language, _corrections, () => _settings.Settings.ResponseLanguage);
        _commands = new LanguageCommands(_corrections, () => _settings.Settings.ResponseLanguage, SetResponseLanguage);
        SelectReplyLanguage(_settings.Settings.ResponseLanguage);

        AddMessage(
            "ShoppyAI",
            "Hello. I am ready to inspect your project and work with Git.");

        foreach (var problem in new[] { _settings.LoadError, _corrections.LoadError, _language.UserGlossaryError })
        {
            if (problem is not null)
                AddMessage("ShoppyAI", $"Warning: {problem}");
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var text = MessageBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(text) || _reply is not null)
            return;

        AddMessage("You", text);
        MessageBox.Clear();

        if (_commands.TryExecute(text) is { } commandResult)
        {
            AddMessage("ShoppyAI", commandResult);
            return;
        }

        await ReplyAsync(text);
    }

    private async Task ReplyAsync(string text)
    {
        using var reply = new CancellationTokenSource();
        _reply = reply;
        SendButton.IsEnabled = false;
        TypingIndicator.Visibility = Visibility.Visible;
        ScrollToEnd();

        var answer = new StringBuilder();
        ContentControl? bubble = null;
        var lastRender = DateTime.MinValue;

        try
        {
            await foreach (var delta in _session.StreamReplyAsync(text, reply.Token))
            {
                answer.Append(delta);
                TypingIndicator.Visibility = Visibility.Collapsed;
                bubble ??= AddAnswerMessage();

                if (DateTime.UtcNow - lastRender >= RenderInterval)
                {
                    bubble.Content = MarkdownRenderer.Render(answer.ToString());
                    lastRender = DateTime.UtcNow;
                    ScrollToEnd();
                }
            }

            if (bubble is null)
                AddMessage("ShoppyAI", "The model returned an empty answer. Please try again.");
            else
                bubble.Content = MarkdownRenderer.Render(answer.ToString());
        }
        catch (OperationCanceledException) when (reply.IsCancellationRequested)
        {
            // New chat while answering: the old answer is dropped.
        }
        catch (Exception error)
        {
            if (bubble is not null)
                bubble.Content = MarkdownRenderer.Render(answer.ToString());

            var message = error is TaskCanceledException
                ? "Ollama did not answer in time."
                : error.Message;
            AddMessage("ShoppyAI", $"{(bubble is null ? "" : "The answer was interrupted. ")}{message}");
        }
        finally
        {
            _reply = null;
            TypingIndicator.Visibility = Visibility.Collapsed;
            SendButton.IsEnabled = true;
            ScrollToEnd();
        }
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _reply?.Cancel();
        _session.Reset();
        MessagesPanel.Children.Clear();
        AddMessage("ShoppyAI", "New conversation started.");
    }

    private void ProjectFiles_Click(object sender, RoutedEventArgs e)
    {
        AddMessage("ShoppyAI", "Project file scanning will be connected next.");
    }

    private void GitStatus_Click(object sender, RoutedEventArgs e)
    {
        AddMessage("ShoppyAI", "Git status will be connected next.");
    }

    private void ReplyLanguage_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse<ResponseLanguage>(tag, out var language))
            SetResponseLanguage(language);
    }

    private void LanguageCorrections_Click(object sender, RoutedEventArgs e)
    {
        new LanguageCorrectionsWindow(_corrections) { Owner = this }.ShowDialog();
    }

    private void MessageBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        MarkdownRenderer.ApplyDirection(MessageBox, MessageBox.Text);
    }

    private void SetResponseLanguage(ResponseLanguage language)
    {
        SelectReplyLanguage(language);

        if (_settings.Settings.ResponseLanguage == language)
            return;

        _settings.Settings.ResponseLanguage = language;
        try
        {
            _settings.Save();
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            AddMessage("ShoppyAI", $"Warning: the language choice could not be saved ({error.Message}).");
        }
    }

    private void SelectReplyLanguage(ResponseLanguage language)
    {
        foreach (var radio in ReplyLanguagePanel.Children.OfType<RadioButton>())
        {
            if (radio.Tag as string == language.ToString())
                radio.IsChecked = true;
        }
    }

    private void AddMessage(string author, string message)
    {
        var text = new TextBlock
        {
            Text = message,
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap
        };
        MarkdownRenderer.ApplyDirection(text, message);

        AddMessagePanel(author, text);
    }

    private ContentControl AddAnswerMessage()
    {
        var content = new ContentControl();
        AddMessagePanel("ShoppyAI", content);
        return content;
    }

    private void AddMessagePanel(string author, FrameworkElement body)
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

        body.Margin = new Thickness(0, 5, 0, 0);
        panel.Children.Add(body);

        MessagesPanel.Children.Add(panel);
        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        MessagesScroll.ScrollToEnd();
    }
}
