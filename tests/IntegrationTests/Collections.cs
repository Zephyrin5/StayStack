namespace IntegrationTests;

// One collection per module, each on a database of its own, copied from the assembly's migrated
// template (PostgresFixture). Collections are what xUnit runs in parallel, so the split is what
// makes the suite parallel and the separate databases are what make it safe.
//
// Each has a named fixture type rather than one shared type, because xUnit matches a test class's
// constructor parameter against the exact fixture type the collection registers. The name in the
// constructor is therefore also the statement of which database that test writes to.

[CollectionDefinition(Name)]
public sealed class AuthCollection : ICollectionFixture<AuthFixture>
{
    public const string Name = "Auth";
}

public sealed class AuthFixture(PostgresFixture postgres) : IntegrationTestWebApplicationFactory(postgres, "auth");

[CollectionDefinition(Name)]
public sealed class BookingsCollection : ICollectionFixture<BookingsFixture>
{
    public const string Name = "Bookings";
}

public sealed class BookingsFixture(PostgresFixture postgres) : IntegrationTestWebApplicationFactory(postgres, "bookings");

[CollectionDefinition(Name)]
public sealed class CatalogCollection : ICollectionFixture<CatalogFixture>
{
    public const string Name = "Catalog";
}

public sealed class CatalogFixture(PostgresFixture postgres) : IntegrationTestWebApplicationFactory(postgres, "catalog");

[CollectionDefinition(Name)]
public sealed class HoldsCollection : ICollectionFixture<HoldsFixture>
{
    public const string Name = "Holds";
}

public sealed class HoldsFixture(PostgresFixture postgres) : IntegrationTestWebApplicationFactory(postgres, "holds");

[CollectionDefinition(Name)]
public sealed class PromotionsCollection : ICollectionFixture<PromotionsFixture>
{
    public const string Name = "Promotions";
}

public sealed class PromotionsFixture(PostgresFixture postgres) : IntegrationTestWebApplicationFactory(postgres, "promotions");

[CollectionDefinition(Name)]
public sealed class ReviewsCollection : ICollectionFixture<ReviewsFixture>
{
    public const string Name = "Reviews";
}

public sealed class ReviewsFixture(PostgresFixture postgres) : IntegrationTestWebApplicationFactory(postgres, "reviews");

[CollectionDefinition(Name)]
public sealed class TransactionsCollection : ICollectionFixture<TransactionsFixture>
{
    public const string Name = "Transactions";
}

public sealed class TransactionsFixture(PostgresFixture postgres) : IntegrationTestWebApplicationFactory(postgres, "transactions");

/// <summary>
///     What belongs to no single module: the pipeline, configuration, serialization, health, and the
///     two modules with a handful of tests each.
/// </summary>
[CollectionDefinition(Name)]
public sealed class CommonCollection : ICollectionFixture<CommonFixture>
{
    public const string Name = "Common";
}

public sealed class CommonFixture(PostgresFixture postgres) : IntegrationTestWebApplicationFactory(postgres, "common");

/// <summary>
///     The localized-input rule, applied across Catalog's and Hosts's endpoints. Its own collection
///     because every case stands up a host, a property and a unit of its own, which made it the
///     longest-running thing in Common and so the length of the whole run.
/// </summary>
[CollectionDefinition(Name)]
public sealed class LocalizationCollection : ICollectionFixture<LocalizationFixture>
{
    public const string Name = "Localization";
}

public sealed class LocalizationFixture(PostgresFixture postgres)
    : IntegrationTestWebApplicationFactory(postgres, "localization");

/// <summary>
///     The measurements, alone. They count connections and transactions against one server, so a
///     peer collection's pool would be part of every number they report.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MeasurementsCollection : ICollectionFixture<MeasurementsFixture>
{
    public const string Name = "Measurements";
}

/// <summary>
///     The one host that schedules jobs: TwoHostSchedulerProbe exists to watch two hosts share one
///     cron, which a host with the scheduler off cannot show.
/// </summary>
public sealed class MeasurementsFixture(PostgresFixture postgres)
    : IntegrationTestWebApplicationFactory(postgres, "measurements")
{
    protected override bool RunsScheduledJobs => true;
}
