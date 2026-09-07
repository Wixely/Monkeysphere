namespace Monkeysphere.Core;

public sealed class ConcurrencyConflictException(string message) : InvalidOperationException(message);
