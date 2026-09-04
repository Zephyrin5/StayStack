using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Migrations
{
    /// <summary>
    ///     Removes the seeded administrator account - admin@staystack.com,
    ///     password "1234" - that every migrated deployment came up with.
    ///     <para>
    ///         The account was created by HasData, so it is part of the schema
    ///         rather than of anyone's setup: the hash sat in source control,
    ///         the password was in a comment beside it, and the account held
    ///         the Administrator role. Anything that ran migrations had it.
    ///     </para>
    ///     <para>
    ///         Conditional on the password still being the shipped one. If an
    ///         operator has changed it, the account is theirs and this leaves
    ///         it alone - deleting it would take away the administrator they
    ///         actually use. If it is unchanged, nobody is relying on it as a
    ///         real account and the credential is public.
    ///     </para>
    ///     <para>
    ///         A deployment whose only administrator is the seeded one, still
    ///         with the seeded password, needs to create a real administrator
    ///         before applying this.
    ///     </para>
    /// </summary>
    public partial class RemoveSeededAdministratorAccount : Migration
    {
        private const string SeededUserId = "01a00c51-f14f-750e-a0a1-c9f4d4e0f3b5";
        private const string SeededRoleId = "01a00be7-ddff-7598-bfaa-234b454b4029";

        // The hash of "1234" as shipped. Matched, never written - it is here
        // to identify the untouched account, not to recreate it.
        private const string SeededPasswordHash =
            "AQAAAAIAAYagAAAAEOwwat4brQdWXuI6BwAY37PmMmCAc6UjfADvztT2EEXv2r3ynbvkfOJsrCIZYVzDEA==";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Role link first, while the user row is still there to match on.
            migrationBuilder.Sql($"""
                                  DELETE FROM "user_roles" ur
                                  WHERE ur.role_id = '{SeededRoleId}'
                                    AND ur.user_id = '{SeededUserId}'
                                    AND EXISTS (
                                        SELECT 1 FROM "users" u
                                        WHERE u.id = ur.user_id
                                          AND u.password_hash = '{SeededPasswordHash}');
                                  """);

            migrationBuilder.Sql($"""
                                  DELETE FROM "users"
                                  WHERE id = '{SeededUserId}'
                                    AND password_hash = '{SeededPasswordHash}';
                                  """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. EF scaffolded an InsertData here that would
            // put the account back, password and all - a rollback of an
            // unrelated later migration would then quietly restore a known
            // credential, which is the whole thing this migration exists to
            // remove. Reversing the schema should not reverse a security fix,
            // and nothing about the tables themselves changed here to undo.
        }
    }
}
