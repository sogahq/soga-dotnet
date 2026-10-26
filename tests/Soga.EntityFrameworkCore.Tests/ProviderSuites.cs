using Xunit;

namespace Soga.EntityFrameworkCore.Tests;

// Every store suite runs unchanged against every provider. SQLite always runs. SQL Server and PostgreSQL run when
// SOGA_TEST_SQLSERVER / SOGA_TEST_POSTGRES point at a server that may create databases, and are skipped otherwise.

public sealed class SqliteEfSogaStoreTests() : EfSogaStoreTestsBase(new SqliteFixture());

public sealed class SqlServerEfSogaStoreTests() : EfSogaStoreTestsBase(new SqlServerFixture());

public sealed class PostgresEfSogaStoreTests() : EfSogaStoreTestsBase(new PostgresFixture());

public sealed class SqliteEfGroupStoreTests() : EfGroupStoreTestsBase(new SqliteFixture());

public sealed class SqlServerEfGroupStoreTests() : EfGroupStoreTestsBase(new SqlServerFixture());

public sealed class PostgresEfGroupStoreTests() : EfGroupStoreTestsBase(new PostgresFixture());

public sealed class SqliteEfIdentityTests() : EfIdentityTestsBase(new SqliteFixture());

public sealed class SqlServerEfIdentityTests() : EfIdentityTestsBase(new SqlServerFixture());

public sealed class PostgresEfIdentityTests() : EfIdentityTestsBase(new PostgresFixture());

// SQLite has a single writer, so concurrency guarantees are only meaningful on a server database.
public sealed class SqlServerEfConcurrencyTests() : EfConcurrencyTestsBase(new SqlServerFixture());

public sealed class PostgresEfConcurrencyTests() : EfConcurrencyTestsBase(new PostgresFixture());
