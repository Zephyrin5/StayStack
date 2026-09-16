using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Database.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "bookings");

            migrationBuilder.EnsureSchema(
                name: "reviews");

            migrationBuilder.EnsureSchema(
                name: "hosts");

            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.EnsureSchema(
                name: "promotions");

            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.EnsureSchema(
                name: "transactions");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "booking_management_tokens",
                schema: "bookings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    booking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_booking_management_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bookings",
                schema: "bookings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hold_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    guest_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    guest_email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    guest_phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    check_in = table.Column<DateOnly>(type: "date", nullable: false),
                    check_out = table.Column<DateOnly>(type: "date", nullable: false),
                    guest_count = table.Column<int>(type: "integer", nullable: false),
                    booking_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payment_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancellation_policy = table.Column<string>(type: "jsonb", nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    subtotal = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    total_price = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bookings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "checkout_idempotency_records",
                schema: "bookings",
                columns: table => new
                {
                    booking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_hash = table.Column<string>(type: "character varying(44)", maxLength: 44, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(44)", maxLength: 44, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_checkout_idempotency_records", x => x.booking_id);
                });

            migrationBuilder.CreateTable(
                name: "guest_reviews",
                schema: "reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    booking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    overall_rating = table.Column<int>(type: "integer", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guest_reviews", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "hosts",
                schema: "hosts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    contact_email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    contact_phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    display_name = table.Column<string>(type: "jsonb", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hosts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pricing_rules",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    date_range = table.Column<NpgsqlRange<DateOnly>>(type: "daterange", nullable: true),
                    override_price = table.Column<decimal>(type: "numeric(12,3)", nullable: true),
                    days_of_week = table.Column<int[]>(type: "integer[]", nullable: true),
                    multiplier = table.Column<decimal>(type: "numeric(5,3)", nullable: true),
                    min_nights = table.Column<int>(type: "integer", nullable: true),
                    discount_percent = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pricing_rules", x => x.id);
                    table.CheckConstraint("ck_pricing_rules_days_of_week_domain", "rule_type <> 'DayOfWeekMultiplier' OR (cardinality(days_of_week) > 0 AND days_of_week <@ ARRAY[0,1,2,3,4,5,6])");
                });

            migrationBuilder.CreateTable(
                name: "promotion_redemptions",
                schema: "promotions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    booking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    redeemed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reversed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    discount_amount = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promotion_redemptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "promotions",
                schema: "promotions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    discount_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    discount_value = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    host_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    max_redemptions = table.Column<int>(type: "integer", nullable: true),
                    redemption_count = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promotions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "properties",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    host_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "jsonb", nullable: false),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_properties", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_revoked = table.Column<bool>(type: "boolean", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    replaced_by_token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "refund_obligations",
                schema: "bookings",
                columns: table => new
                {
                    booking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    policy_refund_amount = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    cause = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refund_obligations", x => x.booking_id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stay_reviews",
                schema: "reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    booking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewer_guest_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    cleanliness_rating = table.Column<int>(type: "integer", nullable: false),
                    communication_rating = table.Column<int>(type: "integer", nullable: false),
                    location_rating = table.Column<int>(type: "integer", nullable: false),
                    value_rating = table.Column<int>(type: "integer", nullable: false),
                    accuracy_rating = table.Column<int>(type: "integer", nullable: false),
                    overall_rating = table.Column<decimal>(type: "numeric(3,2)", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true),
                    host_reply_text = table.Column<string>(type: "text", nullable: true),
                    host_replied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stay_reviews", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "transactions",
                schema: "transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    booking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    succeeded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refund_cause = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    refund_amount = table.Column<decimal>(type: "numeric(12,3)", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transactions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "unit_availability_holds",
                schema: "bookings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest_count = table.Column<int>(type: "integer", nullable: false),
                    stay_range = table.Column<NpgsqlRange<DateOnly>>(type: "daterange", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    hold_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    booked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    client_key = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    subtotal = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    length_of_stay_discount_amount = table.Column<decimal>(type: "numeric(12,3)", nullable: true),
                    total_price = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit_availability_holds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "units",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "jsonb", nullable: false),
                    max_occupancy = table.Column<int>(type: "integer", nullable: false),
                    cancellation_policy = table.Column<string>(type: "jsonb", nullable: false),
                    base_price = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_units", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    host_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    security_stamp = table.Column<string>(type: "text", nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true),
                    phone_number = table.Column<string>(type: "text", nullable: true),
                    phone_number_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lockout_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    access_failed_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_logins",
                schema: "identity",
                columns: table => new
                {
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    provider_display_name = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_logins", x => new { x.login_provider, x.provider_key });
                    table.ForeignKey(
                        name: "fk_user_logins_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                schema: "identity",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_user_roles_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "identity",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_roles_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_tokens",
                schema: "identity",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_tokens", x => new { x.user_id, x.login_provider, x.name });
                    table.ForeignKey(
                        name: "fk_user_tokens_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "identity",
                table: "roles",
                columns: new[] { "id", "concurrency_stamp", "name", "normalized_name" },
                values: new object[,]
                {
                    { new Guid("01a00be7-ddff-7598-bfaa-234b454b4029"), "7cfd1da2-9854-4677-87ee-b6659ac06491", "Administrator", "ADMINISTRATOR" },
                    { new Guid("01a00be7-ddff-7598-bfaa-256e7999a546"), "57622f33-b382-4978-928e-c7c362208dcc", "Host", "HOST" },
                    { new Guid("01a00be7-ddff-7598-bfaa-2b4b9a219feb"), "b84be7ca-95b3-464a-9437-f152271e1f2d", "PropertyStaff", "PROPERTYSTAFF" },
                    { new Guid("01a00be7-ddff-7598-bfaa-2de215ccf970"), "f86f1503-e374-4bb8-9eae-405c9f4e0803", "Customer", "CUSTOMER" }
                });

            migrationBuilder.CreateIndex(
                name: "ix_booking_management_tokens_booking_id",
                schema: "bookings",
                table: "booking_management_tokens",
                column: "booking_id");

            migrationBuilder.CreateIndex(
                name: "ix_booking_management_tokens_token_hash",
                schema: "bookings",
                table: "booking_management_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bookings_customer_id",
                schema: "bookings",
                table: "bookings",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_bookings_hold_id",
                schema: "bookings",
                table: "bookings",
                column: "hold_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bookings_payment_due_at",
                schema: "bookings",
                table: "bookings",
                column: "payment_due_at",
                filter: "booking_status = 'Pending' AND payment_due_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_bookings_unit_id",
                schema: "bookings",
                table: "bookings",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_checkout_idempotency_created_at",
                schema: "bookings",
                table: "checkout_idempotency_records",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_checkout_idempotency_key_hash",
                schema: "bookings",
                table: "checkout_idempotency_records",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_guest_reviews_booking_id",
                schema: "reviews",
                table: "guest_reviews",
                column: "booking_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_guest_reviews_host_id",
                schema: "reviews",
                table: "guest_reviews",
                column: "host_id");

            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_day_of_week_0_active",
                schema: "catalog",
                table: "pricing_rules",
                column: "unit_id",
                unique: true,
                filter: "rule_type = 'DayOfWeekMultiplier' AND status <> 2 AND days_of_week @> ARRAY[0]");

            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_day_of_week_1_active",
                schema: "catalog",
                table: "pricing_rules",
                column: "unit_id",
                unique: true,
                filter: "rule_type = 'DayOfWeekMultiplier' AND status <> 2 AND days_of_week @> ARRAY[1]");

            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_day_of_week_2_active",
                schema: "catalog",
                table: "pricing_rules",
                column: "unit_id",
                unique: true,
                filter: "rule_type = 'DayOfWeekMultiplier' AND status <> 2 AND days_of_week @> ARRAY[2]");

            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_day_of_week_3_active",
                schema: "catalog",
                table: "pricing_rules",
                column: "unit_id",
                unique: true,
                filter: "rule_type = 'DayOfWeekMultiplier' AND status <> 2 AND days_of_week @> ARRAY[3]");

            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_day_of_week_4_active",
                schema: "catalog",
                table: "pricing_rules",
                column: "unit_id",
                unique: true,
                filter: "rule_type = 'DayOfWeekMultiplier' AND status <> 2 AND days_of_week @> ARRAY[4]");

            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_day_of_week_5_active",
                schema: "catalog",
                table: "pricing_rules",
                column: "unit_id",
                unique: true,
                filter: "rule_type = 'DayOfWeekMultiplier' AND status <> 2 AND days_of_week @> ARRAY[5]");

            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_day_of_week_6_active",
                schema: "catalog",
                table: "pricing_rules",
                column: "unit_id",
                unique: true,
                filter: "rule_type = 'DayOfWeekMultiplier' AND status <> 2 AND days_of_week @> ARRAY[6]");

            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_length_of_stay_active",
                schema: "catalog",
                table: "pricing_rules",
                column: "unit_id",
                unique: true,
                filter: "rule_type = 'LengthOfStayDiscount' AND status <> 2");

            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_type",
                schema: "catalog",
                table: "pricing_rules",
                columns: new[] { "unit_id", "rule_type" });

            migrationBuilder.CreateIndex(
                name: "ix_promotion_redemptions_booking_id",
                schema: "promotions",
                table: "promotion_redemptions",
                column: "booking_id");

            migrationBuilder.CreateIndex(
                name: "ix_promotion_redemptions_promotion_email",
                schema: "promotions",
                table: "promotion_redemptions",
                columns: new[] { "promotion_id", "guest_email" },
                unique: true,
                filter: "reversed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_promotions_code",
                schema: "promotions",
                table: "promotions",
                column: "code",
                unique: true,
                filter: "status <> 2");

            migrationBuilder.CreateIndex(
                name: "ix_promotions_host_id",
                schema: "promotions",
                table: "promotions",
                column: "host_id");

            migrationBuilder.CreateIndex(
                name: "ix_properties_city_trgm",
                schema: "catalog",
                table: "properties",
                column: "city")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_properties_host_id",
                schema: "catalog",
                table: "properties",
                column: "host_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_expires_at",
                schema: "identity",
                table: "refresh_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_family_id",
                schema: "identity",
                table: "refresh_tokens",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_hash",
                schema: "identity",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refund_obligations_unresolved",
                schema: "bookings",
                table: "refund_obligations",
                column: "next_attempt_at",
                filter: "resolved_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                schema: "identity",
                table: "roles",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stay_reviews_booking_id",
                schema: "reviews",
                table: "stay_reviews",
                column: "booking_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stay_reviews_host_id",
                schema: "reviews",
                table: "stay_reviews",
                column: "host_id");

            migrationBuilder.CreateIndex(
                name: "ix_stay_reviews_property_id",
                schema: "reviews",
                table: "stay_reviews",
                column: "property_id");

            migrationBuilder.CreateIndex(
                name: "ix_transactions_booking_id",
                schema: "transactions",
                table: "transactions",
                column: "booking_id");

            migrationBuilder.CreateIndex(
                name: "ix_transactions_booking_id_active",
                schema: "transactions",
                table: "transactions",
                column: "booking_id",
                unique: true,
                filter: "transaction_status IN ('Pending', 'Succeeded')");

            migrationBuilder.CreateIndex(
                name: "ix_unit_availability_holds_client_key_active",
                schema: "bookings",
                table: "unit_availability_holds",
                column: "client_key",
                filter: "status IN ('held', 'pending_payment')");

            migrationBuilder.CreateIndex(
                name: "ix_unit_availability_holds_hold_expires_at",
                schema: "bookings",
                table: "unit_availability_holds",
                column: "hold_expires_at",
                filter: "status = 'held'");

            migrationBuilder.CreateIndex(
                name: "ix_unit_availability_holds_unit_id",
                schema: "bookings",
                table: "unit_availability_holds",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_units_property_id",
                schema: "catalog",
                table: "units",
                column: "property_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_logins_user_id",
                schema: "identity",
                table: "user_logins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_role_id",
                schema: "identity",
                table: "user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                schema: "identity",
                table: "users",
                column: "normalized_email");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                schema: "identity",
                table: "users",
                column: "normalized_user_name",
                unique: true);
            // The three objects the model cannot express, and the only hand-written SQL here. Npgsql's
            // EF Core provider has no fluent API for EXCLUDE USING gist (npgsql/efcore.pg#1975), so the
            // two overlap constraints are written out; nothing in the C# model represents them, and
            // SchemaInvariantsTests and PricingRuleConstraintTests are what catch their loss.

            // No two active holds on one unit may overlap - the double-booking guard (docs/adr/0010).
            migrationBuilder.Sql("""
                                 ALTER TABLE bookings.unit_availability_holds
                                 ADD CONSTRAINT unit_availability_holds_overlap_excl
                                 EXCLUDE USING gist (unit_id WITH =, stay_range WITH &&);
                                 """);

            // No two active date-range overrides for one unit may overlap. PricingCalculator resolves a
            // nightly price with FirstOrDefault over an unordered list, so at-most-one-match is a
            // precondition of the read path rather than of one writer (docs/adr/0011). The WHERE mirrors
            // PricingRuleOverlapChecker: only DateRangeOverride rows carry a date_range, and an archived
            // rule must not block its own replacement (status 2 = Archived; rule_type is text).
            migrationBuilder.Sql("""
                                 ALTER TABLE catalog.pricing_rules
                                 ADD CONSTRAINT pricing_rules_date_range_overlap_excl
                                 EXCLUDE USING gist (unit_id WITH =, date_range WITH &&)
                                 WHERE (rule_type = 'DateRangeOverride' AND status <> 2);
                                 """);

            // status is a plain varchar compared as a bare literal in raw SQL, EF expressions and partial
            // index filters alike, so the closed set is pinned where the next reader will find it.
            migrationBuilder.Sql("""
                                 ALTER TABLE bookings.unit_availability_holds
                                 ADD CONSTRAINT ck_unit_availability_holds_status
                                 CHECK (status IN ('held', 'pending_payment', 'booked'));
                                 """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "booking_management_tokens",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "bookings",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "checkout_idempotency_records",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "guest_reviews",
                schema: "reviews");

            migrationBuilder.DropTable(
                name: "hosts",
                schema: "hosts");

            migrationBuilder.DropTable(
                name: "pricing_rules",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "promotion_redemptions",
                schema: "promotions");

            migrationBuilder.DropTable(
                name: "promotions",
                schema: "promotions");

            migrationBuilder.DropTable(
                name: "properties",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "refresh_tokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "refund_obligations",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "stay_reviews",
                schema: "reviews");

            migrationBuilder.DropTable(
                name: "transactions",
                schema: "transactions");

            migrationBuilder.DropTable(
                name: "unit_availability_holds",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "units",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "user_logins",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_roles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_tokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "users",
                schema: "identity");
        }
    }
}
