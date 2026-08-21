using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Hosts.Contracts;
using Moq;
using System.Net;
namespace UnitTests.Contracts;

public class HostAuthorizationTests
{
    private readonly Mock<ICurrentUserProvider> _currentUserProviderMock = new Mock<ICurrentUserProvider>();
    private readonly HostAuthorization _sut;

    public HostAuthorizationTests()
    {
        _sut = new HostAuthorization(_currentUserProviderMock.Object);
    }

    [Fact]
    public void RequireHostId_ShouldReturnCallersHostId_WhenPresent()
    {
        Guid hostId = Guid.NewGuid();
        _currentUserProviderMock.Setup(p => p.HostId).Returns(hostId);

        Guid result = _sut.RequireHostId();

        Assert.Equal(hostId, result);
    }

    [Fact]
    public void RequireHostId_ShouldThrowNotAHostException_WhenCallerHasNoHostId()
    {
        _currentUserProviderMock.Setup(p => p.HostId).Returns((Guid?)null);

        NotAHostException exception = Assert.Throws<NotAHostException>(() => _sut.RequireHostId());
        Assert.Equal((int)HttpStatusCode.Forbidden, exception.StatusCode);
    }

    [Fact]
    public void RequireOwnership_ShouldNotThrow_WhenResourceBelongsToCaller()
    {
        Guid hostId = Guid.NewGuid();
        _currentUserProviderMock.Setup(p => p.HostId).Returns(hostId);

        Exception? exception = Record.Exception(() => _sut.RequireOwnership(hostId, "Property", Guid.NewGuid()));

        Assert.Null(exception);
    }

    [Fact]
    public void RequireOwnership_ShouldThrowNotFoundException_WhenResourceBelongsToADifferentHost()
    {
        // Deliberately NotFoundException, not some 403 - see IHostAuthorization's
        // own doc comment on why "exists but isn't yours" must look
        // identical to "doesn't exist" from the caller's perspective.
        _currentUserProviderMock.Setup(p => p.HostId).Returns(Guid.NewGuid());
        Guid resourceHostId = Guid.NewGuid();
        Guid resourceKey = Guid.NewGuid();

        NotFoundException exception = Assert.Throws<NotFoundException>(
            () => _sut.RequireOwnership(resourceHostId, "Property", resourceKey));
        Assert.Equal((int)HttpStatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public void RequireOwnership_ShouldThrowNotAHostException_WhenCallerHasNoHostIdAtAll()
    {
        // RequireOwnership delegates its "who is calling" check to
        // RequireHostId() - a caller with no host at all should surface as
        // NotAHostException, not get as far as the ownership comparison.
        _currentUserProviderMock.Setup(p => p.HostId).Returns((Guid?)null);

        Assert.Throws<NotAHostException>(
            () => _sut.RequireOwnership(Guid.NewGuid(), "Property", Guid.NewGuid()));
    }
}
