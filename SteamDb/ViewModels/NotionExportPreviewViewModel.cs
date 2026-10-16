using SteamDb.Services;
using System.Collections.Generic;

namespace SteamDb.ViewModels;

/// <summary>
/// The Notion dry run shown before anything is written: which pages would be created and which
/// updated (before → after). The user confirms or cancels.
/// </summary>
public sealed class NotionExportPreviewViewModel : ViewModelBase
{
    public NotionExportPreviewViewModel(NotionExportPlan plan)
    {
        ToCreate = plan.ToCreate;
        ToUpdate = plan.ToUpdate;
        Unchanged = plan.Unchanged;
    }

    /// <summary>Design-time only (Avalonia previewer).</summary>
    public NotionExportPreviewViewModel()
        : this(new NotionExportPlan(
            new GameLibraryResult([], false, false, false, false, false, false),
            [new NotionPageCreate("Alan Wake", "Epic", new object())],
            [new NotionPageUpdate("p1", "The Witcher 3", "Steam · Steam:292030",
                "Steam/GOG · Steam:292030; GOG:1207664643", new object())],
            120))
    {
    }

    public IReadOnlyList<NotionPageCreate> ToCreate { get; }

    public IReadOnlyList<NotionPageUpdate> ToUpdate { get; }

    public int Unchanged { get; }

    public bool HasChanges => ToCreate.Count > 0 || ToUpdate.Count > 0;

    public bool HasCreates => ToCreate.Count > 0;

    public bool HasUpdates => ToUpdate.Count > 0;

    public string Summary => HasChanges
        ? $"{ToCreate.Count} to add · {ToUpdate.Count} to update · {Unchanged} unchanged"
        : $"Your Notion database is already up to date ({Unchanged} games).";

    public string CreateHeader => $"New games ({ToCreate.Count})";

    public string UpdateHeader => $"Updated games ({ToUpdate.Count})";

    /// <summary>Label of the confirm button, e.g. "Export 15 changes".</summary>
    public string ConfirmLabel
    {
        get
        {
            var count = ToCreate.Count + ToUpdate.Count;
            return count == 1 ? "Export 1 change" : $"Export {count} changes";
        }
    }
}
