using System.Text.Json;

namespace Monkeysphere.Core.Tests;

public sealed class CommandRequestHashTests
{
    [Fact]
    public void PropertyOrderDoesNotChangeRetryIdentityButArrayOrderAndNullDo()
    {
        using JsonDocument first = JsonDocument.Parse("""{"b":{"x":1,"y":2},"a":[1,2]}""");
        using JsonDocument reordered = JsonDocument.Parse("""{"a":[1,2],"b":{"y":2,"x":1}}""");
        using JsonDocument changedArray = JsonDocument.Parse("""{"a":[2,1],"b":{"y":2,"x":1}}""");
        using JsonDocument addedNull = JsonDocument.Parse("""{"a":[1,2],"b":{"y":2,"x":1},"c":null}""");
        string hash = CommandRequestHash.Compute(first.RootElement);
        Assert.Equal(hash, CommandRequestHash.Compute(reordered.RootElement));
        Assert.NotEqual(hash, CommandRequestHash.Compute(changedArray.RootElement));
        Assert.NotEqual(hash, CommandRequestHash.Compute(addedNull.RootElement));
    }
}
