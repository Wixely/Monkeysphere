using System.Text;
using Monkeysphere.Core;

namespace Monkeysphere.Core.Tests;

public sealed class VCardTests
{
    [Fact]
    public void ParserAcceptsVersionsUnfoldsLinesAndPreservesOpaqueProperties()
    {
        byte[] source = Encoding.UTF8.GetBytes("""
            BEGIN:VCARD
            VERSION:3.0
            FN:Ada\, Countess of Lovelace
            item1.TEL;HOME;TYPE=voice:+44 1234
            item1.X-ABLabel:Family phone
            X-CUSTOM;X-OPTION="one,two":first
             second
            END:VCARD
            BEGIN:VCARD
            VERSION:4.0
            FN:Grace Hopper
            END:VCARD
            """);

        IReadOnlyList<VCard> cards = VCardParser.Parse(source).Cards;

        Assert.Equal(2, cards.Count);
        Assert.Equal("Ada, Countess of Lovelace", Assert.Single(cards[0].Named("FN")).TextValue);
        VCardProperty phone = Assert.Single(cards[0].Named("TEL"));
        Assert.Equal("ITEM1", phone.Group);
        Assert.Equal(2, phone.Parameters.Count);
        Assert.Equal("HOME", phone.Parameters[0].Values[0]);
        Assert.Equal("firstsecond", Assert.Single(cards[0].Named("X-CUSTOM")).Value);
        Assert.Equal(64, cards[0].Fingerprint.Length);
    }

    [Fact]
    public void SerializerWritesVersionFourAndSemanticallyRoundTripsOpaqueValues()
    {
        VCard source = Assert.Single(VCardParser.Parse(Encoding.UTF8.GetBytes("""
            BEGIN:VCARD
            VERSION:3.0
            FN:Zoë Example
            N:Example;Zoë;;;
            X-NOTE;X-LABEL="one,two":Uses\; punctuation
            END:VCARD
            """)).Cards);

        byte[] exported = VCardSerializer.Serialize([source.Properties]);
        string text = Encoding.UTF8.GetString(exported);
        VCard reparsed = Assert.Single(VCardParser.Parse(exported).Cards);

        Assert.Contains("VERSION:4.0\r\n", text, StringComparison.Ordinal);
        Assert.Equal("N:Example;Zoë;;;", VCardSerializer.PropertyLine(Assert.Single(reparsed.Named("N"))));
        Assert.Equal("Uses; punctuation", Assert.Single(reparsed.Named("X-NOTE")).TextValue);
        Assert.All(text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries), line =>
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75));
    }

    // Structural damage still costs the whole file: without matched markers there is no way to
    // tell where one card ends and the next begins, so nothing can be salvaged safely.
    [Theory]
    [InlineData("VERSION:4.0\r\nFN:Ada\r\n")]
    [InlineData(" BEGIN:VCARD\r\n")]
    [InlineData("BEGIN:VCARD\r\nVERSION:4.0\r\nFN:Ada\r\n")]
    [InlineData("BEGIN:VCARD\r\nVERSION:4.0\r\nFN:Ada\r\nBEGIN:VCARD\r\n")]
    public void ParserRejectsStructurallyMalformedInput(string source) =>
        Assert.Throws<DomainValidationException>(() => VCardParser.Parse(Encoding.UTF8.GetBytes(source)));

    // A card the parser cannot make sense of is set aside and reported. Importing 200 contacts
    // must not fail because one of them is unusable, which is what this whole shape exists for.
    [Theory]
    [InlineData("VERSION:2.1\r\nFN:Ada\r\n", "VERSION", "Ada")]
    [InlineData("VERSION:4.0\r\nN:Lovelace;Ada;;;\r\n", "formatted name", "Lovelace Ada")]
    [InlineData("VERSION:4.0\r\nFN:Ada\r\nFN:Ada Again\r\n", "formatted name", "Ada")]
    [InlineData("VERSION:4.0\r\nFN:\r\nORG:Analytical Engines Ltd\r\n", "formatted name", "Analytical Engines Ltd")]
    [InlineData("VERSION:4.0\r\nFN:Ada\r\nBROKEN-NO-COLON\r\n", "delimiter", "Ada")]
    public void AnUnusableCardIsSetAsideAndTheRestOfTheFileStillImports(string bad, string reason, string label)
    {
        string good = "BEGIN:VCARD\r\nVERSION:4.0\r\nFN:Grace Hopper\r\nEND:VCARD\r\n";
        string file = good + "BEGIN:VCARD\r\n" + bad + "END:VCARD\r\n" + good.Replace("Grace Hopper", "Katherine Johnson", StringComparison.Ordinal);

        VCardParseResult result = VCardParser.Parse(Encoding.UTF8.GetBytes(file));

        Assert.Equal(2, result.Cards.Count);
        Assert.Equal(["Grace Hopper", "Katherine Johnson"],
            result.Cards.Select(card => Assert.Single(card.Named("FN")).TextValue));

        VCardRejectedCard rejected = Assert.Single(result.Rejected);
        Assert.Equal(2, rejected.Position);
        Assert.Equal(label, rejected.Label);
        Assert.Contains(reason, rejected.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACardWithNothingIdentifiableIsStillReportedByItsPosition()
    {
        VCardParseResult result = VCardParser.Parse(Encoding.UTF8.GetBytes(
            "BEGIN:VCARD\r\nVERSION:4.0\r\nEND:VCARD\r\n"));

        Assert.Empty(result.Cards);
        VCardRejectedCard rejected = Assert.Single(result.Rejected);
        Assert.Equal(1, rejected.Position);
        Assert.Null(rejected.Label);
    }
}
