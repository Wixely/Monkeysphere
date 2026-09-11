using System.Text;
using Monkeysphere.Core;

namespace Monkeysphere.Core.Tests;

public sealed class VCardStreamTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(16384)]
    public async Task StreamingParserPreservesUtf8FoldingAndOpaqueProperties(int fragmentSize)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Zoë 😀\r\nitem1.TEL;TYPE=home:+44 1234\r\nitem1.X-ABLabel:Family\r\nX-CUSTOM;X-PARAM=\"one,two\":one\r\n two\r\n\tthree\r\nEND:VCARD\r\nBEGIN:VCARD\nVERSION:4.0\nFN:Second\nEND:VCARD");
        IReadOnlyList<VCard> expected = VCardParser.Parse(bytes).Cards;
        using FragmentedStream stream = new(bytes, fragmentSize);
        IReadOnlyList<VCard> actual = (await VCardParser.ParseAsync(stream)).Cards;
        Assert.Equal(expected.Select(card => card.Fingerprint), actual.Select(card => card.Fingerprint));
        Assert.Equal(VCardSerializer.Serialize(expected.Select(card => card.Properties).ToArray()), VCardSerializer.Serialize(actual.Select(card => card.Properties).ToArray()));
        Assert.Equal("onetwothree", Assert.Single(actual[0].Named("X-CUSTOM")).Value);
        Assert.True(stream.CanRead);
        Assert.True(stream.MaximumRequestedBytes <= 16 * 1024);
    }

    [Fact]
    public async Task DecoderHandlesASurrogatePairSplitAcrossFullBuffers()
    {
        const string prefix = "BEGIN:VCARD\r\nVERSION:4.0\r\nFN:";
        byte[] bytes = Encoding.UTF8.GetBytes(prefix + new string('a', 16384 - 3 - prefix.Length) + "😀" + new string('b', 16383) + "\r\nEND:VCARD\r\n");
        using FragmentedStream stream = new(bytes, 16384);
        Assert.Equal(Assert.Single(VCardParser.Parse(bytes).Cards).Fingerprint, Assert.Single((await VCardParser.ParseAsync(stream)).Cards).Fingerprint);
    }

    [Fact]
    public async Task ParserAcceptsTheByteLimitAndRejectsOneByteBeyondIt()
    {
        const string prefix = "BEGIN:VCARD\nVERSION:4.0\nFN:A\nNOTE:";
        const string suffix = "\nEND:VCARD\n";
        byte[] bytes = Encoding.UTF8.GetBytes(prefix + new string('x', VCardParser.MaximumBytes - prefix.Length - suffix.Length) + suffix);
        Assert.Equal(VCardParser.MaximumBytes, bytes.Length);
        using FragmentedStream valid = new(bytes, 16384);
        Assert.Single((await VCardParser.ParseAsync(valid)).Cards);
        using FragmentedStream oversized = new([.. bytes, (byte)'x'], 16384);
        await Assert.ThrowsAsync<DomainValidationException>(() => VCardParser.ParseAsync(oversized));
    }

    [Fact]
    public async Task StreamingParserRejectsMalformedUtf8StructureAndCardLimits()
    {
        byte[] valid = Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nFN:A\nEND:VCARD\n");
        byte[][] invalid = [[], [0xff], [.. valid, 0xc3], Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nFN:A\n"),
            Encoding.UTF8.GetBytes(" BEGIN:VCARD\n"), Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nFN:A\nBEGIN:VCARD\n"),
            Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(Encoding.UTF8.GetString(valid), VCardParser.MaximumCards + 1)))];
        foreach (byte[] bytes in invalid)
        {
            using FragmentedStream stream = new(bytes, 7);
            await Assert.ThrowsAsync<DomainValidationException>(() => VCardParser.ParseAsync(stream));
            Assert.Throws<DomainValidationException>(() => VCardParser.Parse(bytes));
        }
        // An overlong single card is that card's problem, not the file's.
        byte[] overlong = Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nFN:A\n"
            + string.Concat(Enumerable.Repeat("X:A\n", VCardParser.MaximumPropertiesPerCard)) + "END:VCARD\n");
        using FragmentedStream overlongStream = new(overlong, 97);
        VCardParseResult tooMany = await VCardParser.ParseAsync(overlongStream);
        Assert.Empty(tooMany.Cards);
        Assert.Contains("2000", Assert.Single(tooMany.Rejected).Reason, StringComparison.Ordinal);

        using FragmentedStream cancelled = new(valid, 1);
        using CancellationTokenSource token = new();
        token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VCardParser.ParseAsync(cancelled, token.Token));
    }

    [Fact]
    public async Task ManyFoldedLinesKeepTheirContentAndFingerprint()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nFN:A\nNOTE:start" + string.Concat(Enumerable.Repeat("\n x", 50000)) + "\nEND:VCARD\n");
        using FragmentedStream stream = new(bytes, 97);
        VCard parsed = Assert.Single((await VCardParser.ParseAsync(stream)).Cards);
        Assert.Equal("start" + new string('x', 50000), Assert.Single(parsed.Named("NOTE")).Value);
        Assert.Equal(Assert.Single(VCardParser.Parse(bytes).Cards).Fingerprint, parsed.Fingerprint);
    }

    private sealed class FragmentedStream(byte[] bytes, int fragmentSize) : MemoryStream(bytes, writable: false)
    {
        public int MaximumRequestedBytes { get; private set; }
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumRequestedBytes = Math.Max(MaximumRequestedBytes, buffer.Length);
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, fragmentSize)], cancellationToken);
        }
    }
}
