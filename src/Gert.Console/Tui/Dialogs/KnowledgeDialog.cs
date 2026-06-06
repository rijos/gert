using System.Collections.ObjectModel;
using Gert.Console.Tui.State;
using Gert.Model.Rag;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Gert.Console.Tui.Dialogs;

/// <summary>
/// The knowledge panel (U16) — the console analog of
/// <c>knowledge-panel.js</c>: the project's documents (top, <c>a</c>dd a
/// local file path / <c>d</c>elete) and memory entries (bottom, <c>a</c>dd
/// title+content / <c>d</c>elete). Ingestion is inline, so an added document
/// lists with its terminal status immediately.
/// </summary>
public static class KnowledgeDialog
{
    /// <summary>Show modally; data loads happen inside (the modal loop pumps Invoke).</summary>
    public static void Show(IApplication application, KnowledgePresenter presenter)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(presenter);

        var documents = new List<Document>();
        var memory = new List<MemoryEntry>();
        var documentRows = new ObservableCollection<string>();
        var memoryRows = new ObservableCollection<string>();

        using var dialog = new Dialog
        {
            Title = $"Knowledge — {presenter.Pid} (a add · d delete · Esc close)",
            Width = Dim.Percent(70),
            Height = Dim.Percent(70),
        };

        var documentsLabel = new Label { X = 0, Y = 0, Text = "Documents" };
        var documentList = new ListView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Percent(50) - 2,
        };
        documentList.SetSource(documentRows);

        var memoryLabel = new Label { X = 0, Y = Pos.Bottom(documentList), Text = "Memory" };
        var memoryList = new ListView
        {
            X = 0,
            Y = Pos.Bottom(memoryLabel),
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        memoryList.SetSource(memoryRows);

        dialog.Add(documentsLabel, documentList, memoryLabel, memoryList);

        void Refresh() => Task.Run(async () =>
        {
            var docs = await presenter.ListDocumentsAsync().ConfigureAwait(false);
            var entries = await presenter.ListMemoryAsync().ConfigureAwait(false);
            application.Invoke(() =>
            {
                documents.Clear();
                documents.AddRange(docs);
                documentRows.Clear();
                foreach (var doc in docs)
                {
                    documentRows.Add(
                        $"{KnowledgePresenter.DisplayName(doc)} — {doc.Status} ({doc.ChunkCount} chunks)");
                }

                memory.Clear();
                memory.AddRange(entries);
                memoryRows.Clear();
                foreach (var entry in entries)
                {
                    memoryRows.Add($"{entry.Title}{(entry.Pinned ? " 📌" : string.Empty)}");
                }
            });
        });

        void Run(Task task) => task.ContinueWith(
            t => application.Invoke(() => MessageDialog.Show(application, "Knowledge", t.Exception!.GetBaseException().Message)),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        documentList.KeyDown += (_, key) =>
        {
            if (key == Key.A)
            {
                key.Handled = true;
                if (InputDialog.Show(application, "Add document", "Local file path:") is { } path)
                {
                    Run(presenter.UploadAsync(path).ContinueWith(_ => Refresh(), TaskScheduler.Default));
                }
            }
            else if ((key == Key.D || key == Key.Delete)
                && documentList.SelectedItem is { } index && index >= 0 && index < documents.Count)
            {
                key.Handled = true;
                Run(presenter.DeleteDocumentAsync(documents[index].Id)
                    .ContinueWith(_ => Refresh(), TaskScheduler.Default));
            }
        };

        memoryList.KeyDown += (_, key) =>
        {
            if (key == Key.A)
            {
                key.Handled = true;
                if (InputDialog.Show(application, "Add memory", "Title:") is { } title
                    && InputDialog.Show(application, "Add memory", "Content (markdown):") is { } content)
                {
                    Run(presenter.UpsertMemoryAsync(title, content)
                        .ContinueWith(_ => Refresh(), TaskScheduler.Default));
                }
            }
            else if ((key == Key.D || key == Key.Delete)
                && memoryList.SelectedItem is { } index && index >= 0 && index < memory.Count)
            {
                key.Handled = true;
                Run(presenter.DeleteMemoryAsync(memory[index].Id)
                    .ContinueWith(_ => Refresh(), TaskScheduler.Default));
            }
        };

        dialog.KeyDown += (_, key) =>
        {
            if (key == Key.Esc)
            {
                key.Handled = true;
                application.RequestStop(dialog);
            }
        };

        Refresh();
        documentList.SetFocus();
        application.Run(dialog);
    }
}
