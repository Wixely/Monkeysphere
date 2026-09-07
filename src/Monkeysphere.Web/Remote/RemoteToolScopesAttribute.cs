namespace Monkeysphere.Web.Remote;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
public sealed class RemoteToolScopesAttribute(params string[] anyOfScopes) : Attribute
{
    public IReadOnlyList<string> AnyOfScopes { get; } = anyOfScopes;
}
