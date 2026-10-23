using System.Windows;
using System.Windows.Controls;
using ShoppyAI.Core.Language;

namespace ShoppyAI.Desktop;

/// <summary>Lets the user add, edit, disable, delete and undo language corrections.</summary>
public partial class LanguageCorrectionsWindow : Window
{
    private readonly CorrectionStore _store;

    /// <summary>Correction being edited (selected in the list); null when adding a new word.</summary>
    private string? _editingId;

    public LanguageCorrectionsWindow(CorrectionStore store)
    {
        InitializeComponent();
        _store = store;
        Refresh(store.LoadError is null ? null : $"Warning: {store.LoadError}");
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

        Apply(() =>
        {
            var correction = _editingId is not null && _store.Find(_editingId) is not null
                ? _store.Update(_editingId, TermBox.Text, MeaningBox.Text, NoteBox.Text)
                : _store.AddOrUpdate(TermBox.Text, MeaningBox.Text, NoteBox.Text);
            ClearEditor();
            return $"Saved \"{correction.Term}\".";
        });
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        ClearEditor();
        Refresh();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (CorrectionsList.SelectedItem is not LanguageCorrection correction)
        {
            Refresh("Select a correction to delete.");
            return;
        }

        Apply(() =>
        {
            _store.Remove(correction.Id);
            ClearEditor();
            return $"Deleted \"{correction.Term}\". Undo restores it.";
        });
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        Apply(() =>
        {
            ClearEditor();
            return _store.Undo() is { } undone ? $"Undone: {undone}." : "Nothing to undo.";
        });
    }

    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LanguageCorrection correction)
            return;

        Apply(() =>
        {
            _store.SetEnabled(correction.Id, !correction.Enabled);
            return $"\"{correction.Term}\" {(correction.Enabled ? "disabled" : "enabled")}.";
        });
    }

    private void CorrectionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CorrectionsList.SelectedItem is not LanguageCorrection correction)
            return;

        _editingId = correction.Id;
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

    /// <summary>Runs a change and shows its result, or why it failed, without closing the window.</summary>
    private void Apply(Func<string> change)
    {
        string status;
        try
        {
            status = change();
        }
        catch (ArgumentException error)
        {
            status = error.Message.Split(" (Parameter")[0];
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            status = $"The change could not be saved, nothing was changed ({error.Message}).";
        }

        Refresh(status);
    }

    private void ClearEditor()
    {
        _editingId = null;
        CorrectionsList.SelectedItem = null;
        TermBox.Clear();
        MeaningBox.Clear();
        NoteBox.Clear();
    }
}
