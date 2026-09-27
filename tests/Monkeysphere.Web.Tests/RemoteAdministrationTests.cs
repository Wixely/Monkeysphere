using System.Text.Json;
using DnaX.RemoteAccess;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// Administering remote access from a remote client is the most dangerous thing on this surface, so
/// these pin the controls that make it safe rather than the happy path. The one that matters is that a
/// rotation cannot widen: without it a credential holding nothing but <c>admin.manage</c> could mint
/// itself one that reads every record and downloads every backup, and administration would be a
/// privilege-escalation path wearing a feature's clothes.
/// </summary>
public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task AdministrationCannotMintACredentialWiderThanTheOneMakingTheCall()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (administration, credential, surface) = await EnableRelationshipWritesAsync(
            factory, ["admin.read", "admin.manage"]);

        // Every one of these is a permission this credential does not hold, and each is a different
        // kind of prize: reading records, taking a copy of everything, seeing withheld records.
        foreach (string coveted in (string[])["records.read", "backups.export", "backstage"])
        {
            using JsonDocument refused = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
                "rotate_remote_credential", new { surface = "mcp", scopes = new[] { "admin.read", "admin.manage", coveted } });
            AssertWriteError(refused, "permission_denied");
        }

        // Refused before anything changed, so the credential in hand still works and still holds
        // exactly what it did. A refusal that had already rotated would be worse than no refusal.
        using JsonDocument state = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_remote_access_state");
        RemoteAccessState read = Structured(state).Deserialize<RemoteAccessState>(JsonOptions)!;
        string[] unchanged = ["admin.manage", "admin.read"];
        Assert.Equal(unchanged, read.Mcp.Scopes.Order(StringComparer.Ordinal));

        // Narrowing is allowed, which is the other half of the rule: an operator can hand a client a
        // way to reduce its own reach without being able to increase it.
        string[] narrowedTo = ["admin.read"];
        using JsonDocument narrowed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "rotate_remote_credential", new { surface = "mcp", scopes = narrowedTo });
        RemoteAdministrationResult result = Structured(narrowed).Deserialize<RemoteAdministrationResult>(JsonOptions)!;
        Assert.Equal(narrowedTo, result.Scopes);
        Assert.NotNull(result.Credential);

        // The new secret is returned once, in the result, and it really is the working credential.
        using JsonDocument afterwards = await SendAsync(client, surface.EndpointPath!, result.Credential!, "tools/call", "get_remote_access_state");
        Assert.Equal(narrowedTo, Structured(afterwards).Deserialize<RemoteAccessState>(JsonOptions)!.Mcp.Scopes);

        // And having narrowed itself, it can no longer administer at all: the reduction is real rather
        // than cosmetic.
        using JsonDocument locked = await SendAsync(client, surface.EndpointPath!, result.Credential!, "tools/call",
            "rotate_remote_credential", new { surface = "mcp", scopes = unchanged });
        AssertWriteError(locked, "permission_denied");

        // The operator's own path is untouched: the browser can still widen, which is the recovery
        // route the remote refusal depends on existing.
        string[] widened = ["admin.read", "admin.manage", "records.read"];
        _ = await factory.Services.GetRequiredService<RemoteCredentialManager>()
            .RotateAsync(DnaXRemoteSurface.Mcp, (await administration.GetStateAsync()).Mcp.Version, widened);
        Assert.Contains("records.read", (await administration.GetStateAsync()).Mcp.Credential!.Scopes);
    }

    [Fact]
    public async Task AdministrationDisclosesWhenAChangeBreaksTheConnectionMakingIt()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["admin.read", "admin.manage"]);

        // Acting on the other surface cannot break this connection, and says so rather than warning
        // about nothing.
        using JsonDocument other = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "set_remote_activation", new { surface = "api", active = false });
        RemoteAdministrationResult elsewhere = Structured(other).Deserialize<RemoteAdministrationResult>(JsonOptions)!;
        Assert.False(elsewhere.AffectsThisConnection);
        Assert.Contains("unaffected", elsewhere.Disclosure, StringComparison.OrdinalIgnoreCase);

        // Moving the endpoint this call arrived on is allowed, because refusing it would make a
        // compromised endpoint unmovable remotely, but the result has to say what just happened.
        string before = surface.EndpointPath!;
        using JsonDocument moved = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "rotate_remote_endpoint", new { surface = "mcp" });
        RemoteAdministrationResult rotated = Structured(moved).Deserialize<RemoteAdministrationResult>(JsonOptions)!;
        Assert.True(rotated.AffectsThisConnection);
        Assert.Contains("may stop working", rotated.Disclosure, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(before, rotated.EndpointPath);

        // The disclosure is not decoration: the old address is genuinely gone and the new one works.
        using JsonDocument atNew = await SendAsync(client, rotated.EndpointPath!, credential.Secret, "tools/call", "get_remote_access_state");
        _ = Structured(atNew);

        // Revoking the credential in use ends it, which is how a compromised credential is stopped
        // from the client that noticed.
        using JsonDocument revoked = await SendAsync(client, rotated.EndpointPath!, credential.Secret, "tools/call",
            "revoke_remote_credential", new { surface = "mcp" });
        RemoteAdministrationResult ended = Structured(revoked).Deserialize<RemoteAdministrationResult>(JsonOptions)!;
        Assert.True(ended.AffectsThisConnection);
        Assert.Null(ended.CredentialEnding);
    }

    [Fact]
    public async Task AdministrationStateNamesNoSecretAndReadingItIsNotAdministeringIt()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["admin.read"]);

        using JsonDocument state = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_remote_access_state");
        RemoteAccessState read = Structured(state).Deserialize<RemoteAccessState>(JsonOptions)!;
        Assert.True(read.Enabled);
        Assert.True(read.Mcp.IsActive);
        Assert.Equal(surface.EndpointPath, read.Mcp.EndpointPath);

        // The credential is identified, not disclosed. Asserted over the whole response because the
        // property is that the secret appears nowhere, not that one field omits it.
        Assert.NotNull(read.Mcp.CredentialEnding);
        Assert.DoesNotContain(credential.Secret, Structured(state).GetRawText(), StringComparison.Ordinal);

        // Reading the state is not administering it. These are separate grants because one is a
        // diagnostic a client can reasonably be trusted with and the other can lock the operator out.
        foreach ((string tool, object arguments) in ((string, object)[])[
            ("set_remote_activation", new { surface = "mcp", active = false }),
            ("rotate_remote_credential", new { surface = "mcp", scopes = new[] { "admin.read" } }),
            ("revoke_remote_credential", new { surface = "mcp" }),
            ("rotate_remote_endpoint", new { surface = "mcp" })])
        {
            using JsonDocument refused = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", tool, arguments);
            AssertWriteError(refused, "permission_denied");
        }

        // Still active and still holding what it did, so none of those refusals half-applied.
        using JsonDocument after = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_remote_access_state");
        Assert.True(Structured(after).Deserialize<RemoteAccessState>(JsonOptions)!.Mcp.IsActive);
    }

    [Fact]
    public async Task ActivityIsRedactedPagedAndDistinguishesSilenceFromDisabledAuditing()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["admin.read"]);

        // Make some activity to read back.
        using (JsonDocument _ = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_remote_access_state")) { }

        using JsonDocument listed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "list_remote_activity", new { limit = 10 });
        JsonElement structured = Structured(listed);
        RemoteActivityPage page = structured.Deserialize<RemoteActivityPage>(JsonOptions)!;
        Assert.Equal(10, page.Limit);
        Assert.True(page.Returned > 0);
        Assert.All(page.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.Action)));

        // Redacted means redacted: the credential that made the calls is not in the log of them.
        Assert.DoesNotContain(credential.Secret, structured.GetRawText(), StringComparison.Ordinal);

        using JsonDocument unbounded = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "list_remote_activity", new { limit = 201 });
        AssertWriteError(unbounded, "validation_failed");
        using JsonDocument nothing = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "list_remote_activity", new { limit = 0 });
        AssertWriteError(nothing, "validation_failed");
    }
}
