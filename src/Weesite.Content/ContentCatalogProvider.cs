namespace Weesite.Content;

/// <summary>Holds the current catalog; a reload swaps it atomically.</summary>
public sealed class ContentCatalogProvider
{
    private volatile ContentCatalog _current = ContentCatalog.Empty;

    /// <summary>Gets the catalog requests read from.</summary>
    public ContentCatalog Current => _current;

    /// <summary>Gets a value indicating whether content has been loaded at least once.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Replaces the catalog.</summary>
    /// <param name="catalog">The new catalog.</param>
    public void Set(ContentCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _current = catalog;
        IsLoaded = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after the catalog is replaced (a reload), so caches built from it can follow.</summary>
    public event EventHandler? Changed;
}
