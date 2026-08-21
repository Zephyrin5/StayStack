using BuildingBlocks.Observability;
namespace UnitTests.Observability;

public class PayloadRedactorTests
{

    [Fact]
    public void Redact_MasksSensitiveProperties()
    {
        SampleCommand cmd = new SampleCommand("my-secret-password", "public-data");

        string redacted = PayloadRedactor.Redact(cmd);

        Assert.Contains("Secret=[REDACTED]", redacted);
        Assert.Contains("PublicInfo=public-data", redacted);
    }

    private record SampleCommand([property: Sensitive] string Secret, string PublicInfo);
}
