using SteamDb.Services;
using System.Collections.Generic;
using System.Linq;

namespace SteamDb.ViewModels;

/// <summary>The "Find duplicates" result: which pages would be merged, for the user to confirm.</summary>
public sealed class NotionDuplicatesViewModel : ViewModelBase
{
    public NotionDuplicatesViewModel(IReadOnlyList<NotionDuplicateGroup> groups)
    {
        Groups = groups;
    }

    /// <summary>Design-time only (Avalonia previewer).</summary>
    public NotionDuplicatesViewModel() : this([])
    {
    }

    public IReadOnlyList<NotionDuplicateGroup> Groups { get; }

    public bool HasGroups => Groups.Count > 0;

    private int Removed => Groups.Sum(g => g.Remove.Count);

    public string Summary => HasGroups
        ? $"{Groups.Count} games have more than one page. For each, the oldest page is kept and gets all " +
          $"store tags and ids; the other {Removed} page(s) are moved to Notion's trash, where they can be " +
          "restored for 30 days. Notes, status and other columns of the kept page are not changed — copy " +
          "anything you need from the other pages first."
        : "No duplicate pages found: every game has a single page.";

    public string ConfirmLabel => $"Merge {Groups.Count} games";

    public string CancelLabel => HasGroups ? "Cancel" : "Close";
}
