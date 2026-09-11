using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

public sealed partial class DomainIsolationTests
{
    [Fact]
    public async Task ContactImportExpiryRemovesOutcomesThenTombstonesOnLaterWrites()
    {
        string root = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using ServiceProvider provider = RegistryProvider(root);
            await provider.InitializeMonkeysphereDomainsAsync();
            using IServiceScope scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
            UploadOwner owner = new(MonkeysphereDomains.DefaultId, new string('4', 64));
            byte[] bytes = Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nFN:Retention import\nEND:VCARD\n");
            Guid upload = await StagePreviewUploadAsync(provider.GetRequiredService<IRemoteUploadStore>(), owner, bytes);
            ContactImportPreviewService previewService = scope.ServiceProvider.GetRequiredService<ContactImportPreviewService>();
            ContactImportPreviewHandle[] previews = new ContactImportPreviewHandle[3];
            for (int index = 0; index < previews.Length; index++)
                previews[index] = await previewService.CreateAsync(owner, upload, Guid.NewGuid());
            ContactImportCommandService commands = scope.ServiceProvider.GetRequiredService<ContactImportCommandService>();
            Guid firstKey = Guid.NewGuid();
            _ = await commands.ApplyAsync(owner, previews[0].PreviewId, previews[0].Revision, firstKey, [new(0, VCardImportAction.Skip)]);
            await using SqliteConnection connection = await scope.ServiceProvider.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
            await connection.ExecuteAsync("UPDATE ContactImportReceipts SET RetryUntilUtc = '2000-01-01T00:00:00.0000000+00:00';");
            IContactImportCommandStore store = scope.ServiceProvider.GetRequiredService<IContactImportCommandStore>();
            Assert.Equal("retry_expired", (await Assert.ThrowsAsync<CommandReplayException>(() => store.GetAsync(owner, firstKey))).Code);
            _ = await commands.ApplyAsync(owner, previews[1].PreviewId, previews[1].Revision, Guid.NewGuid(), [new(0, VCardImportAction.Skip)]);
            Assert.Equal(1, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM ContactImportOutcomes;"));
            Assert.Equal(2, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM ContactImportReceipts;"));
            await connection.ExecuteAsync("UPDATE ContactImportReceipts SET ForgetAfterUtc = '2000-01-01T00:00:00.0000000+00:00' WHERE IdempotencyKey = @Key;",
                new { Key = firstKey.ToString("D") });
            _ = await commands.ApplyAsync(owner, previews[2].PreviewId, previews[2].Revision, Guid.NewGuid(), [new(0, VCardImportAction.Skip)]);
            Assert.Equal(2, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM ContactImportReceipts;"));
            await Assert.ThrowsAsync<RecordCommandNotFoundException>(() => store.GetAsync(owner, firstKey));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("commands")]
    [InlineData("outcomes")]
    public async Task ContactImportQuotasRejectNewCommandsAndKeepExistingReceiptReplayable(string dimension)
    {
        string root = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using ServiceProvider provider = RegistryProvider(root);
            await provider.InitializeMonkeysphereDomainsAsync();
            using IServiceScope scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
            UploadOwner owner = new(MonkeysphereDomains.DefaultId, new string('3', 64));
            byte[] bytes = Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nFN:Quota import\nEND:VCARD\n");
            Guid upload = await StagePreviewUploadAsync(provider.GetRequiredService<IRemoteUploadStore>(), owner, bytes);
            ContactImportPreviewService previewService = scope.ServiceProvider.GetRequiredService<ContactImportPreviewService>();
            ContactImportCommandService commands = scope.ServiceProvider.GetRequiredService<ContactImportCommandService>();
            ContactImportPreviewHandle firstPreview = await previewService.CreateAsync(owner, upload, Guid.NewGuid());
            Guid firstKey = Guid.NewGuid();
            ContactImportReceipt firstReceipt = await commands.ApplyAsync(owner, firstPreview.PreviewId, firstPreview.Revision,
                firstKey, [new(0, VCardImportAction.Skip)]);
            ContactImportPreviewHandle nextPreview = await previewService.CreateAsync(owner, upload, Guid.NewGuid());
            await using SqliteConnection connection = await scope.ServiceProvider.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
            int receiptCount = dimension == "commands" ? 999 : 100;
            await connection.ExecuteAsync("""
                WITH RECURSIVE N(Value) AS (SELECT 1 UNION ALL SELECT Value + 1 FROM N WHERE Value < @Count)
                INSERT INTO ContactImportReceipts
                    (Id, CredentialFingerprint, IdempotencyKey, PreviewId, RequestHash, Created, Merged, Replaced, Skipped,
                     ContactCount, CompletedAtUtc, RetryUntilUtc, ForgetAfterUtc)
                SELECT 'quota-receipt-' || Value, 'quota-owner-' || Value, 'quota-key-' || Value, 'quota-preview-' || Value,
                    @Hash, 0, 0, 0, 1, CASE WHEN @Dimension = 'outcomes' THEN 1000 ELSE 1 END,
                    @Now, '2099-01-01T00:00:00.0000000+00:00', '2099-01-08T00:00:00.0000000+00:00' FROM N;
                """, new { Count = receiptCount, Dimension = dimension, Hash = new string('A', 64), Now = DateTimeOffset.UtcNow.ToString("O") });
            if (dimension == "outcomes")
            {
                await connection.ExecuteAsync("""
                    WITH RECURSIVE R(Value) AS (SELECT 1 UNION ALL SELECT Value + 1 FROM R WHERE Value < 100),
                                   I(Value) AS (SELECT 0 UNION ALL SELECT Value + 1 FROM I WHERE Value < 999)
                    INSERT INTO ContactImportOutcomes (ReceiptId, ContactIndex, Action, RecordId, Revision)
                    SELECT 'quota-receipt-' || R.Value, I.Value, 1, NULL, NULL FROM R CROSS JOIN I
                    WHERE R.Value < 100 OR I.Value < 999;
                    """);
                Assert.Equal(ContactImportCommandLimits.MaximumRetainedOutcomesPerDomain,
                    await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM ContactImportOutcomes;"));
            }
            else
            {
                Assert.Equal(ContactImportCommandLimits.MaximumRetainedCommandsPerDomain,
                    await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM ContactImportReceipts;"));
            }

            Assert.Equal("limit_exceeded", (await Assert.ThrowsAsync<CommandReplayException>(() => commands.ApplyAsync(
                owner, nextPreview.PreviewId, nextPreview.Revision, Guid.NewGuid(), [new(0, VCardImportAction.Skip)]))).Code);
            Assert.Equal(firstReceipt, await commands.ApplyAsync(owner, firstPreview.PreviewId, firstPreview.Revision,
                firstKey, [new(0, VCardImportAction.Skip)]));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ContactImportRetainsOneThousandPagedOutcomes()
    {
        string root = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using ServiceProvider provider = RegistryProvider(root);
            await provider.InitializeMonkeysphereDomainsAsync();
            using IServiceScope scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
            StringBuilder vcard = new();
            for (int index = 0; index < VCardParser.MaximumCards; index++)
            {
                vcard.Append("BEGIN:VCARD\nVERSION:4.0\nFN:Outcome ").Append(index).Append("\nEND:VCARD\n");
            }

            byte[] bytes = Encoding.UTF8.GetBytes(vcard.ToString());
            UploadOwner owner = new(MonkeysphereDomains.DefaultId, new string('2', 64));
            Guid upload = await StagePreviewUploadAsync(provider.GetRequiredService<IRemoteUploadStore>(), owner, bytes);
            ContactImportPreviewHandle preview = await scope.ServiceProvider.GetRequiredService<ContactImportPreviewService>()
                .CreateAsync(owner, upload, Guid.NewGuid());
            Assert.Equal(VCardParser.MaximumCards, preview.ContactCount);
            VCardImportSelection[] selections = Enumerable.Range(0, VCardParser.MaximumCards)
                .Select(index => new VCardImportSelection(index, VCardImportAction.Skip)).ToArray();
            Guid key = Guid.NewGuid();
            ContactImportReceipt receipt = await scope.ServiceProvider.GetRequiredService<ContactImportCommandService>()
                .ApplyAsync(owner, preview.PreviewId, preview.Revision, key, selections);
            Assert.Equal(VCardParser.MaximumCards, receipt.Result.Skipped);
            IContactImportCommandStore store = scope.ServiceProvider.GetRequiredService<IContactImportCommandStore>();
            for (int page = 1; page <= 10; page++)
            {
                IReadOnlyList<ContactImportOutcome> outcomes = await store.ReadOutcomesAsync(owner, key, page, 100);
                Assert.Equal(100, outcomes.Count);
                Assert.Equal((page - 1) * 100, outcomes[0].ContactIndex);
                Assert.All(outcomes, outcome => Assert.Equal(VCardImportAction.Skip, outcome.Action));
            }
            Assert.Empty(await store.ReadOutcomesAsync(owner, key, 11, 100));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ContactImportCommitsAllActionsOutcomesAndReceiptAndReplaysAfterSourceCancellation()
    {
        string root = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            UploadOwner owner = new(MonkeysphereDomains.DefaultId, new string('F', 64), "import-correlation");
            Guid importKey = Guid.NewGuid();
            ContactImportReceipt receipt;
            await using (ServiceProvider provider = RegistryProvider(root))
            {
                await provider.InitializeMonkeysphereDomainsAsync();
                using IServiceScope scope = provider.CreateScope();
                IPresetService presets = scope.ServiceProvider.GetRequiredService<IPresetService>();
                await presets.InstallPresetAsync("monkeysphere.person");
                IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
                Guid personType = (await records.ListRecordTypesAsync()).Single(type => type.PresetKey == "monkeysphere.person").Id;
                RecordDetails mergeTarget = await records.CreateRecordAsync(personType, "Merge target", []);
                RecordDetails replaceTarget = await records.CreateRecordAsync(personType, "Replace target", []);
                byte[] bytes = Encoding.UTF8.GetBytes("""
                    BEGIN:VCARD
                    VERSION:4.0
                    FN:Created contact
                    X-OPAQUE:preserved
                    END:VCARD
                    BEGIN:VCARD
                    VERSION:4.0
                    FN:Skipped contact
                    END:VCARD
                    BEGIN:VCARD
                    VERSION:4.0
                    FN:Merge target
                    EMAIL:merge@example.test
                    END:VCARD
                    BEGIN:VCARD
                    VERSION:4.0
                    FN:Replace target
                    EMAIL:replace@example.test
                    END:VCARD
                    """.Replace("\r\n", "\n", StringComparison.Ordinal));
                IRemoteUploadStore uploads = provider.GetRequiredService<IRemoteUploadStore>();
                Guid uploadId = await StagePreviewUploadAsync(uploads, owner, bytes);
                ContactImportPreviewHandle preview = await scope.ServiceProvider.GetRequiredService<ContactImportPreviewService>()
                    .CreateAsync(owner, uploadId, Guid.NewGuid());
                VCardImportSelection[] selections =
                [
                    new(0, VCardImportAction.CreateSeparately),
                    new(1, VCardImportAction.Skip),
                    new(2, VCardImportAction.MergeNonConflicting, mergeTarget.Record.Id),
                    new(3, VCardImportAction.ReplaceMappedValues, replaceTarget.Record.Id),
                ];
                ContactImportCommandService commands = scope.ServiceProvider.GetRequiredService<ContactImportCommandService>();
                ContactImportReceipt[] concurrent = await Task.WhenAll(
                    Task.Run(() => commands.ApplyAsync(owner, preview.PreviewId, preview.Revision, importKey, selections)),
                    Task.Run(() => commands.ApplyAsync(owner, preview.PreviewId, preview.Revision, importKey, selections)));
                Assert.Equal(concurrent[0], concurrent[1]);
                receipt = concurrent[0];
                Assert.Equal(new(1, 1, 1, 1), receipt.Result);
                Assert.Equal(4, receipt.ContactCount);
                Assert.Equal(3, (await records.SearchRecordsAsync(new())).TotalCount);

                await using SqliteConnection application = await scope.ServiceProvider.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
                Assert.Equal("X-OPAQUE", await application.QuerySingleAsync<string>("SELECT Name FROM RecordSourceValues WHERE Name = 'X-OPAQUE';"));

                IContactImportCommandStore store = scope.ServiceProvider.GetRequiredService<IContactImportCommandStore>();
                ContactImportOutcome[] outcomes = (await store.ReadOutcomesAsync(owner, importKey, 1, 100)).ToArray();
                Assert.Equal(4, outcomes.Length);
                Assert.Equal(VCardImportAction.CreateSeparately, outcomes[0].Action);
                Assert.NotNull(outcomes[0].RecordId);
                Assert.Matches("^[0-9a-f]{32}$", outcomes[0].Revision!);
                Assert.Null(outcomes[1].RecordId);
                Assert.Null(outcomes[1].Revision);
                Assert.Equal(mergeTarget.Record.Id, outcomes[2].RecordId);
                Assert.Equal(replaceTarget.Record.Id, outcomes[3].RecordId);
                Assert.Equal(outcomes.Take(2), await store.ReadOutcomesAsync(owner, importKey, 1, 2));
                Assert.Equal(outcomes.Skip(2), await store.ReadOutcomesAsync(owner, importKey, 2, 2));
                Assert.Empty(await store.ReadOutcomesAsync(owner, importKey, 3, 2));

                _ = await uploads.CancelAsync(owner, uploadId);
                Assert.Equal(receipt, await commands.ApplyAsync(owner, preview.PreviewId, preview.Revision, importKey, selections));
                Assert.Equal(receipt, await store.GetAsync(owner, importKey));
                VCardImportSelection[] changed = [.. selections];
                changed[0] = new(0, VCardImportAction.Skip);
                Assert.Equal("retry_conflict", (await Assert.ThrowsAsync<CommandReplayException>(() =>
                    commands.ApplyAsync(owner, preview.PreviewId, preview.Revision, importKey, changed))).Code);
                Assert.Equal("preview_consumed", (await Assert.ThrowsAsync<CommandReplayException>(() =>
                    commands.ApplyAsync(owner, preview.PreviewId, preview.Revision, Guid.NewGuid(), selections))).Code);
            }

            await using (ServiceProvider provider = RegistryProvider(root))
            {
                await provider.InitializeMonkeysphereDomainsAsync();
                using IServiceScope scope = provider.CreateScope();
                IContactImportCommandStore store = scope.ServiceProvider.GetRequiredService<IContactImportCommandStore>();
                Assert.Equal(receipt, await store.GetAsync(owner, importKey));
                Assert.Equal(4, (await store.ReadOutcomesAsync(owner, importKey, 1, 100)).Count);
                await Assert.ThrowsAsync<RecordCommandNotFoundException>(() =>
                    store.GetAsync(owner with { CredentialFingerprint = new string('0', 64) }, importKey));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ContactImportRejectsStalePreviewAndAuditFailureRollsBackEverything()
    {
        string root = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using ServiceProvider provider = RegistryProvider(root);
            await provider.InitializeMonkeysphereDomainsAsync();
            using IServiceScope scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
            UploadOwner owner = new(MonkeysphereDomains.DefaultId, new string('1', 64));
            byte[] bytes = Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nFN:Atomic import\nX-OPAQUE:rollback-me\nEND:VCARD\n");
            IRemoteUploadStore uploads = provider.GetRequiredService<IRemoteUploadStore>();
            ContactImportPreviewService previewService = scope.ServiceProvider.GetRequiredService<ContactImportPreviewService>();
            ContactImportCommandService commands = scope.ServiceProvider.GetRequiredService<ContactImportCommandService>();
            Guid staleUpload = await StagePreviewUploadAsync(uploads, owner, bytes);
            ContactImportPreviewHandle stale = await previewService.CreateAsync(owner, staleUpload, Guid.NewGuid());
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            _ = await records.CreateRecordAsync(stale.RecordTypeId, "Concurrent contact", []);
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => commands.ApplyAsync(owner, stale.PreviewId, stale.Revision,
                Guid.NewGuid(), [new(0, VCardImportAction.CreateSeparately)]));

            Guid upload = await StagePreviewUploadAsync(uploads, owner, bytes);
            ContactImportPreviewHandle preview = await previewService.CreateAsync(owner, upload, Guid.NewGuid());
            Guid key = Guid.NewGuid();
            await using SqliteConnection connection = await scope.ServiceProvider.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
            await connection.ExecuteAsync("""
                CREATE TRIGGER TestFailContactImportAudit BEFORE INSERT ON ApplicationCommandAudit
                WHEN NEW.Action = 'contacts.import' BEGIN SELECT RAISE(ABORT, 'Test failure'); END;
                """);
            await Assert.ThrowsAsync<SqliteException>(() => commands.ApplyAsync(owner, preview.PreviewId, preview.Revision,
                key, [new(0, VCardImportAction.CreateSeparately)]));
            Assert.Equal(1, (await records.SearchRecordsAsync(new())).TotalCount);
            Assert.Equal(0, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM ContactImportReceipts;"));
            Assert.Equal(0, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM ContactImportOutcomes;"));
            Assert.Equal(0, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM RecordSourceImports;"));
            Assert.Equal(0, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM RecordSourceValues;"));
            await connection.ExecuteAsync("DROP TRIGGER TestFailContactImportAudit;");
            ContactImportReceipt receipt = await commands.ApplyAsync(owner, preview.PreviewId, preview.Revision,
                key, [new(0, VCardImportAction.CreateSeparately)]);
            Assert.Equal(1, receipt.Result.Created);
            Assert.Equal(2, (await records.SearchRecordsAsync(new())).TotalCount);
            Assert.Equal(1, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM ApplicationCommandAudit WHERE Action = 'contacts.import';"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
