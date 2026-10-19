namespace RevitMCP;

// Used on the Revit API thread. A closed document is an invalid native wrapper:
// even hashing it to remove a dictionary key can invoke the destroyed object.
internal sealed class DocumentRevisionTracker<TDocument>(Func<TDocument, bool> isValid,
    Func<TDocument, TDocument, bool> sameDocument) where TDocument : class
{
    private sealed class Entry(TDocument document)
    {
        public readonly TDocument Document = document;
        public long Revision;
    }

    private readonly List<Entry> entries = [];

    public long Get(TDocument document) => Find(document).Revision;
    public void Changed(TDocument document) => Find(document).Revision++;
    public void Clear() => entries.Clear();

    private Entry Find(TDocument document)
    {
        // Remove by managed list index, never by Document.GetHashCode/Equals.
        for (var i = entries.Count - 1; i >= 0; i--)
            if (!isValid(entries[i].Document)) entries.RemoveAt(i);
        if (!isValid(document)) throw new InvalidOperationException("The Revit document is closed. Prepare a preview in an open document.");
        // Different managed wrappers can represent the same live Revit session.
        // Reference identity, file names and hash codes alone cannot identify it.
        foreach (var entry in entries)
            if (sameDocument(entry.Document, document)) return entry;
        var created = new Entry(document);
        entries.Add(created);
        return created;
    }
}
