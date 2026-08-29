using Monkeysphere.Core;

namespace Monkeysphere.Core.Tests;

public sealed class FieldTypesTests
{
    [Theory]
    [InlineData("Text", "text")]
    [InlineData("custom.measurement", "custom.measurement")]
    [InlineData("custom_field-2", "custom_field-2")]
    public void NormalizeTypeIdAcceptsOpenIdentifiers(string input, string expected)
    {
        Assert.Equal(expected, FieldTypes.NormalizeTypeId(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2text")]
    [InlineData("contains space")]
    [InlineData("contains/slash")]
    public void NormalizeTypeIdRejectsInvalidIdentifiers(string input)
    {
        Assert.Throws<DomainValidationException>(() => FieldTypes.NormalizeTypeId(input));
    }

    [Fact]
    public void ChoiceConfigurationRejectsCaseInsensitiveDuplicates()
    {
        Assert.Throws<DomainValidationException>(() =>
            FieldTypes.NormalizeConfiguration(FieldTypes.Choice, ["Friend", "friend"]));
    }
}
