using IntegrationTests;

// One container and one migrated template for the assembly; every collection copies a database from
// it (PostgresFixture).
[assembly: AssemblyFixture(typeof(PostgresFixture))]

// Collections run in parallel, which is the reason each has its own database. The switch and the
// thread count are in xunit.runner.json next to this file: this suite is only safe to parallelise
// because of that split, so the setting is stated rather than inherited from a default that could
// change underneath it.
//
// Four threads, not the core count xUnit would pick. What these tests wait on is one Postgres
// container, so past a handful of collections the threads contend rather than overlap - measured at
// eight on a sixteen-core machine: 40s on one run, 50s on the next, against a steady 48s at four.
// It is also a number a two-core CI runner can honour.
