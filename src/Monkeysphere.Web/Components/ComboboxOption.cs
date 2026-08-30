namespace Monkeysphere.Web.Components;

public sealed record ComboboxOption<TValue>(TValue Value, string Label, bool Disabled = false);
