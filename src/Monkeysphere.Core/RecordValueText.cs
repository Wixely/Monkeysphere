namespace Monkeysphere.Core;

/// <summary>
/// One stored value as a line of text.
///
/// This was private to the records grid until a gallery needed the same thing under its collages. Two
/// copies would agree today and disagree the first time a field type gains a way of being read, and
/// the same value reading differently in two views of the same records is the kind of inconsistency
/// nobody reports and everybody notices.
/// </summary>
public static class RecordValueText
{
    public static string Format(RecordValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Tags.Count > 0)
        {
            return string.Join(", ", value.Tags);
        }

        if (value.TemporalValue is not null)
        {
            // The approximation marker belongs to the value: a date somebody recorded as roughly
            // right should not read as exact anywhere it is shown.
            return (value.IsApproximate ? "≈ " : string.Empty) + value.TemporalValue;
        }

        if (value.Location is not null)
        {
            return LocationValues.Format(value.Location);
        }

        return value.TextValue ?? value.NumberValue ?? value.DateValue ?? string.Empty;
    }
}
