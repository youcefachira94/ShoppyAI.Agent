using System.Windows;
using System.Windows.Controls;
using ShoppyAI.Core.Language;

namespace ShoppyAI.Desktop;

/// <summary>Lets the user add, edit, disable, delete and undo language corrections.</summary>
public partial class LanguageCorrectionsWindow : Window
{
    private readonly CorrectionStore _store;

    public LanguageCorrectionsWindow(CorrectionStore store)
    {
        InitializeComponent();
        _store = store;
        Refresh();
    }

    private void Refresh(string? status = null)
    {
        CorrectionsList.ItemsSource = _store.All;
        UndoButton.IsEnabled = _store.CanUndo;
        StatusText.Text = status ?? (_store.All.Count == 0 ? "No corrections yet." : "");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TermBox.Text) || string.IsNullOrWhiteSpace(MeaningBox.Text))
        {
            Refresh("Enter a word and its meaning.");
            return;
        }

        var correction = _store.AddOrUpdate(TermBox.Text, MeaningBox.Text, NoteBox.Text);
        TermBox.Clear();
        MeaningBox.Clear();
        NoteBox.Clear();
        Refresh($"Saved \"{correction.Term}\".");
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (CorrectionsList.SelectedItem is not LanguageCorrection correction)
        {
            Refresh("Select a correction to delete.");
            return;
        }

        _store.Remove(correction.Id);
        Refresh($"Deleted \"{correction.Term}\". Undo restores it.");
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        Refresh(_store.Undo() is { } undone ? $"Undone: {undone}." : "Nothing to undo.");
    }

    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LanguageCorrection correction)
            return;

        _store.SetEnabled(correction.Id, !correction.Enabled);
        Refresh($"\"{correction.Term}\" {(correction.Enabled ? "disabled" : "enabled")}.");
    }

    private void CorrectionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CorrectionsList.SelectedItem is not LanguageCorrection correction)
            return;

        TermBox.Text = correction.Term;
        MeaningBox.Text = correction.Meaning;
        NoteBox.Text = correction.Note ?? "";
    }

    private void Field_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box)
            MarkdownRenderer.ApplyDirection(box, box.Text);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
