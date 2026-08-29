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

    [Theory]
    [InlineData("19", TemporalPrecision.Century, "19", "1801-01-01T00:00:00")]
    [InlineData("1980s", TemporalPrecision.Decade, "1980", "1980-01-01T00:00:00")]
    [InlineData("1815", TemporalPrecision.Year, "1815", "1815-01-01T00:00:00")]
    [InlineData("1815-12", TemporalPrecision.Month, "1815-12", "1815-12-01T00:00:00")]
    [InlineData("1815-12-10", TemporalPrecision.Day, "1815-12-10", "1815-12-10T00:00:00")]
    [InlineData("2026-08-29T10:42", TemporalPrecision.Minute, "2026-08-29T10:42", "2026-08-29T10:42:00")]
    [InlineData("2026-08-29T10:42:31", TemporalPrecision.Second, "2026-08-29T10:42:31", "2026-08-29T10:42:31")]
    public void TemporalValuesPreserveSelectedPrecision(
        string input,
        TemporalPrecision precision,
        string expectedValue,
        string expectedSortKey)
    {
        NormalizedTemporalValue normalized = TemporalValues.Normalize(
            new TemporalValueInput(input, precision, true, "family recollection"),
            "When");

        Assert.Equal(expectedValue, normalized.Value);
        Assert.Equal(expectedSortKey, normalized.SortKey);
        Assert.True(normalized.IsApproximate);
        Assert.Equal("family recollection", normalized.ApproximationNote);
    }

    [Theory]
    [InlineData("0", TemporalPrecision.Century)]
    [InlineData("1985", TemporalPrecision.Decade)]
    [InlineData("15/12/1815", TemporalPrecision.Day)]
    [InlineData("2026-08-29T10:42:31", TemporalPrecision.Minute)]
    public void TemporalValuesRejectValuesThatDoNotMatchTheirPrecision(string value, TemporalPrecision precision)
    {
        Assert.Throws<DomainValidationException>(() =>
            TemporalValues.Normalize(new TemporalValueInput(value, precision), "When"));
    }

    [Theory]
    [InlineData("19c", "1801-01-01T00:00:00")]
    [InlineData("1980s", "1980-01-01T00:00:00")]
    [InlineData("1985", "1985-01-01T00:00:00")]
    [InlineData("2026-08-29T10:42:31", "2026-08-29T10:42:31")]
    public void TemporalFiltersNormalizeToSortableKeys(string input, string expected)
    {
        Assert.Equal(expected, TemporalValues.NormalizeFilterSortKey(input));
    }
}
