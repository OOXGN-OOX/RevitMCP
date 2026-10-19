using RevitMCP;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
    Console.WriteLine("PASS " + message);
}
void Reject(Action action, string message)
{
    try { action(); }
    catch (InvalidOperationException) { Check(true, message); return; }
    throw new Exception(message);
}
var revisions = new DocumentRevisionTracker<DocumentWrapper>(d => d.IsValid, (a, b) => a.Equals(b));
var projectSession = new Session();
var project = new DocumentWrapper(projectSession);
var alias = new DocumentWrapper(projectSession);
Check(revisions.Get(project) == 0, "new session starts at revision zero");
revisions.Changed(project);
Check(revisions.Get(alias) == 1, "different wrappers of one native document share revision");
var other = new DocumentWrapper(new Session());
revisions.Changed(other);
revisions.Changed(other);
Check(revisions.Get(project) == 1 && revisions.Get(other) == 2, "same name/path and hash do not merge different sessions");

var temp = new DocumentWrapper(new Session());
revisions.Changed(temp);
var previewRevision = revisions.Get(project);
temp.Close();
Check(revisions.Get(alias) == previewRevision, "closing temporary document preserves a pending project preview");
revisions.Changed(alias);
Check(revisions.Get(project) != previewRevision, "real project edit still invalidates the old preview");
Check(temp.HashCalls == 0 && temp.EqualityCalls == 0, "closed wrapper is removed without hashing or equality calls");

for (var i = 0; i < 100; i++)
{
    var temporary = new DocumentWrapper(new Session());
    revisions.Changed(temporary);
    temporary.Close();
    if (revisions.Get(project) != 2) throw new Exception("Temporary file revision leaked into project");
}
Check(true, "repeated family open/change/close cycles keep project preview state usable");
Reject(() => revisions.Get(temp), "closed current document is rejected explicitly");
Check(revisions.Get(project) == 2, "rejected closed document does not poison later requests");
project.Close();
var reopened = new DocumentWrapper(new Session());
Check(revisions.Get(reopened) == 0, "reopening the same file starts a new session");
other.Close();
reopened.Close();
revisions.Clear();
Check(revisions.Get(new DocumentWrapper(new Session())) == 0, "module shutdown clears invalid wrappers without native calls");

// Reproduce the previous implementation's failure on the same invalid wrapper.
var oldKey = new DocumentWrapper(new Session());
var oldRegistry = new Dictionary<DocumentWrapper, long> { [oldKey] = 1 };
oldKey.Close();
Reject(() => oldRegistry.Remove(oldKey), "regression fixture reproduces Dictionary.Remove on a closed native document");
Console.WriteLine($"PASS: {checks} document revision checks");

sealed class Session { public bool Open = true; }
sealed class DocumentWrapper(Session session)
{
    public bool IsValid => session.Open;
    public int HashCalls, EqualityCalls;
    public void Close() => session.Open = false;
    public override int GetHashCode()
    {
        HashCalls++;
        if (!IsValid) throw new InvalidOperationException("Native document is no longer valid");
        return 7; // Deliberate collisions; equality must distinguish live sessions.
    }
    public override bool Equals(object? obj)
    {
        EqualityCalls++;
        if (!IsValid || obj is DocumentWrapper { IsValid: false }) throw new InvalidOperationException("Native document is no longer valid");
        return obj is DocumentWrapper other && ReferenceEquals(session, other.NativeSession);
    }
    private Session NativeSession => session;
}
