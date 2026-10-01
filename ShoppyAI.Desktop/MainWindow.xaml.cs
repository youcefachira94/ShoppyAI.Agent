using System.Windows;
using System.Windows.Controls;

namespace ShoppyAI.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        AddMessage(
            "ShoppyAI",
            "Hello. I am ready to inspect your project and work with Git.");
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        var text = MessageBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(text))
            return;

        AddMessage("You", text);
        MessageBox.Clear();

        AddMessage(
            "ShoppyAI",
            "Your message was received. The coding engine will be connected next.");
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
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
}