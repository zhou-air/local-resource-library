using LocalResourceLibrary.Core;

namespace LocalResourceLibrary.WinUI.Services;

/// <summary>Rejects stale fetches and preserves fields edited by the user, including deliberately empty fields.</summary>
internal sealed class UrlEditorDraft
{
    public UrlEditorDraft(ResourceItem? item = null)
    {
        Target = item?.Target ?? "";
        Alias = item?.Alias ?? "";
        Description = item?.Description ?? "";
        Favicon = item?.Favicon;
        AliasEdited = DescriptionEdited = item != null;
    }

    public string Target { get; private set; }
    public string Alias { get; private set; }
    public string Description { get; private set; }
    public byte[]? Favicon { get; private set; }
    public int Revision { get; private set; }
    public bool AliasEdited { get; private set; }
    public bool DescriptionEdited { get; private set; }

    public void SetTarget(string target)
    {
        if (Target == target) return;
        Target = target;
        Revision++;
        Favicon = null;
        if (!AliasEdited) Alias = "";
        if (!DescriptionEdited) Description = "";
    }

    public void EditAlias(string value) { Alias = value; AliasEdited = true; }
    public void EditDescription(string value) { Description = value; DescriptionEdited = true; }

    public bool Apply(int revision, string target, WebsiteMetadata metadata)
    {
        if (revision != Revision || target != Target) return false;
        if (!AliasEdited && metadata.Title != null) Alias = metadata.Title;
        if (!DescriptionEdited && metadata.Description != null) Description = metadata.Description;
        // Preserve an existing cached icon when re-fetching the same URL fails.
        if (metadata.Favicon != null) Favicon = metadata.Favicon;
        return true;
    }
}
