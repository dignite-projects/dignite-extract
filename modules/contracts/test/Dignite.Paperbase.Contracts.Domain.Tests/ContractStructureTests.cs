using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Contracts;

public class ContractStructureTests
{
    [Fact]
    public void Contract_Must_Have_DocumentId_Field()
    {
        typeof(Contract).GetProperty("DocumentId").ShouldNotBeNull();
    }
}
