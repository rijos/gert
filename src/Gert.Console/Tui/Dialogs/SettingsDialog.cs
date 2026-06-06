using System.Globalization;
using Gert.Model.Dtos;
using Gert.Model.Projects;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Gert.Console.Tui.Dialogs;

/// <summary>
/// The settings dialog (U16) — the console analog of
/// <c>settings-modal.js</c> + the per-model cogwheel: reply language, default
/// model id, and one model's GenerationParams (temperature / top_p /
/// max_tokens; blank inherits). Returns the partial update to save, or null
/// on cancel.
/// </summary>
public static class SettingsDialog
{
    /// <summary>Show modally over the current settings; null = cancelled.</summary>
    public static UpdateSettingsRequest? Show(IApplication application, UserSettings current)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(current);

        UpdateSettingsRequest? result = null;

        using var dialog = new Dialog
        {
            Title = "Settings",
            Width = Dim.Percent(60),
            Height = 14,
        };

        var replyField = AddField(dialog, 1, "Reply language:", current.ReplyLanguage ?? string.Empty);
        var modelField = AddField(dialog, 3, "Default model id:", current.DefaultModelId ?? string.Empty);
        var paramsModelField = AddField(dialog, 5, "Params for model:", current.DefaultModelId ?? string.Empty);

        var initial = current.DefaultModelId is { } id
            && current.ModelParams is { } map
            && map.TryGetValue(id, out var existing)
                ? existing
                : null;
        var temperatureField = AddField(
            dialog, 7, "Temperature:", initial?.Temperature?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        var topPField = AddField(
            dialog, 9, "Top P:", initial?.TopP?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        var maxTokensField = AddField(
            dialog, 11, "Max tokens:", initial?.MaxTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

        var save = new Button { Title = "_Save", IsDefault = true };
        save.Accepting += (_, e) =>
        {
            e.Handled = true;
            result = Build(
                replyField.Text,
                modelField.Text,
                paramsModelField.Text,
                temperatureField.Text,
                topPField.Text,
                maxTokensField.Text);
            application.RequestStop(dialog);
        };

        var cancel = new Button { Title = "Cancel" };
        cancel.Accepting += (_, e) =>
        {
            e.Handled = true;
            application.RequestStop(dialog);
        };

        dialog.AddButton(save);
        dialog.AddButton(cancel);
        replyField.SetFocus();

        application.Run(dialog);
        return result;
    }

    private static TextField AddField(Dialog dialog, int y, string label, string initial)
    {
        dialog.Add(new Label { X = 1, Y = y, Text = label });
        var field = new TextField
        {
            X = 20,
            Y = y,
            Width = Dim.Fill(1),
            Text = initial,
        };
        dialog.Add(field);
        return field;
    }

    private static UpdateSettingsRequest Build(
        string reply,
        string model,
        string paramsModel,
        string temperature,
        string topP,
        string maxTokens)
    {
        IReadOnlyDictionary<string, GenerationParams>? modelParams = null;
        if (paramsModel.Trim() is { Length: > 0 } paramsId)
        {
            // An all-blank params object REPLACES (clears) that model's entry —
            // the documented merge semantics of UpdateSettingsRequest.
            modelParams = new Dictionary<string, GenerationParams>
            {
                [paramsId] = new GenerationParams
                {
                    Temperature = ParseDouble(temperature),
                    TopP = ParseDouble(topP),
                    MaxTokens = ParseInt(maxTokens),
                },
            };
        }

        return new UpdateSettingsRequest
        {
            ReplyLanguage = reply.Trim() is { Length: > 0 } r ? r : null,
            DefaultModelId = model.Trim() is { Length: > 0 } m ? m : null,
            ModelParams = modelParams,
        };
    }

    private static double? ParseDouble(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static int? ParseInt(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
