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

        IReadOnlyList<VCard> cards = VCardParser.Parse(source);

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
            """)));

        byte[] exported = VCardSerializer.Serialize([source.Properties]);
        string text = Encoding.UTF8.GetString(exported);
        VCard reparsed = Assert.Single(VCardParser.Parse(exported));

        Assert.Contains("VERSION:4.0\r\n", text, StringComparison.Ordinal);
        Assert.Equal("N:Example;Zoë;;;", VCardSerializer.PropertyLine(Assert.Single(reparsed.Named("N"))));
        Assert.Equal("Uses; punctuation", Assert.Single(reparsed.Named("X-NOTE")).TextValue);
        Assert.All(text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries), line =>
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75));
    }

    [Theory]
    [InlineData("BEGIN:VCARD\r\nVERSION:2.1\r\nFN:Ada\r\nEND:VCARD\r\n")]
    [InlineData("BEGIN:VCARD\r\nVERSION:4.0\r\nEND:VCARD\r\n")]
    [InlineData("VERSION:4.0\r\nFN:Ada\r\n")]
    [InlineData(" BEGIN:VCARD\r\n")]
    public void ParserRejectsUnsupportedOrMalformedInput(string source) =>
        Assert.Throws<DomainValidationException>(() => VCardParser.Parse(Encoding.UTF8.GetBytes(source)));
}
