using BuildingBlocks.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Persistence;
using Persistence.Interceptors;
using SeedWork.Abstractions;
namespace UnitTests.Persistence;

public class AuditableEntitySaveChangesInterceptorTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new SqliteConnection("DataSource=:memory:");

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
    }
    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private DbContextOptions<TestDbContext> CreateOptions(AuditableEntitySaveChangesInterceptor interceptor)
    {
        return new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;
    }

    [Fact]
    public async Task SavingChangesAsync_HandlesNullUserContext_WhenUnauthenticated()
    {
        // Arrange: User ID is null (e.g., background job or anonymous request)
        DateTimeOffset fixedTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var currentUserMock = new Mock<ICurrentUserProvider>();
        currentUserMock.Setup(x => x.UserId).Returns((Guid?)null);

        var timeProviderMock = new Mock<TimeProvider>();
        timeProviderMock.Setup(x => x.GetUtcNow()).Returns(fixedTime);

        AuditableEntitySaveChangesInterceptor interceptor =
            new AuditableEntitySaveChangesInterceptor(currentUserMock.Object, timeProviderMock.Object);

        await using TestDbContext context = new TestDbContext(CreateOptions(interceptor));
        await context.Database.EnsureCreatedAsync();

        TestAuditableEntity entity = new TestAuditableEntity();

        // Act
        context.TestEntities.Add(entity);
        await context.SaveChangesAsync();

        // Assert
        Assert.Equal(fixedTime, entity.CreatedAt);
        Assert.Null(entity.CreatedBy);
    }

    [Fact]
    public async Task SavingChangesAsync_IgnoresNonAuditableEntities()
    {
        // Arrange
        var currentUserMock = new Mock<ICurrentUserProvider>();
        var timeProviderMock = new Mock<TimeProvider>();
        AuditableEntitySaveChangesInterceptor interceptor =
            new AuditableEntitySaveChangesInterceptor(currentUserMock.Object, timeProviderMock.Object);

        await using TestDbContext context = new TestDbContext(CreateOptions(interceptor));
        await context.Database.EnsureCreatedAsync();

        NonAuditableEntity nonAuditable = new NonAuditableEntity { Name = "Standard Record" };

        // Act
        context.NonAuditableEntities.Add(nonAuditable);
        int recordCount = await context.SaveChangesAsync();

        // Assert
        Assert.Equal(1, recordCount);
        currentUserMock.Verify(x => x.UserId, Times.Never);
        timeProviderMock.Verify(x => x.GetUtcNow(), Times.Never);
    }

    [Fact]
    public async Task SavingChangesAsync_DoesNotMutateAuditFields_WhenEntityIsUnchanged()
    {
        // Arrange
        DateTimeOffset initialTime = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        Guid initialUserId = Guid.NewGuid();

        var currentUserMock = new Mock<ICurrentUserProvider>();
        currentUserMock.Setup(x => x.UserId).Returns(initialUserId);

        var timeProviderMock = new Mock<TimeProvider>();
        timeProviderMock.Setup(x => x.GetUtcNow()).Returns(initialTime);

        AuditableEntitySaveChangesInterceptor interceptor =
            new AuditableEntitySaveChangesInterceptor(currentUserMock.Object, timeProviderMock.Object);

        await using TestDbContext context = new TestDbContext(CreateOptions(interceptor));
        await context.Database.EnsureCreatedAsync();

        TestAuditableEntity entity = new TestAuditableEntity();
        context.TestEntities.Add(entity);
        await context.SaveChangesAsync();

        // Act: Save without making changes
        await context.SaveChangesAsync();

        // Assert
        Assert.Null(entity.ModifiedAt);
        Assert.Null(entity.ModifiedBy);
    }

    [Fact]
    public void SavingChanges_SynchronousCall_UpdatesAuditFieldsCorrectly()
    {
        // Arrange: Verify synchronous SaveChanges() overload works identically
        DateTimeOffset fixedTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        Guid userId = Guid.NewGuid();

        var currentUserMock = new Mock<ICurrentUserProvider>();
        currentUserMock.Setup(x => x.UserId).Returns(userId);

        var timeProviderMock = new Mock<TimeProvider>();
        timeProviderMock.Setup(x => x.GetUtcNow()).Returns(fixedTime);

        AuditableEntitySaveChangesInterceptor interceptor =
            new AuditableEntitySaveChangesInterceptor(currentUserMock.Object, timeProviderMock.Object);

        using TestDbContext context = new TestDbContext(CreateOptions(interceptor));
        context.Database.EnsureCreated();

        TestAuditableEntity entity = new TestAuditableEntity();

        // Act
        context.TestEntities.Add(entity);
        context.SaveChanges();

        // Assert
        Assert.Equal(fixedTime, entity.CreatedAt);
        Assert.Equal(userId, entity.CreatedBy);
    }

    // --- Test Harness ---

    public class TestAuditableEntity : Entity
    {
    }

    public class NonAuditableEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TestAuditableEntity> TestEntities => Set<TestAuditableEntity>();
        public DbSet<NonAuditableEntity> NonAuditableEntities => Set<NonAuditableEntity>();
    }
}
