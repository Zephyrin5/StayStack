using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reviews.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewsModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,");

            migrationBuilder.CreateTable(
                name: "guest_reviews",
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
                name: "stay_reviews",
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

            migrationBuilder.CreateIndex(
                name: "ix_guest_reviews_booking_id",
                table: "guest_reviews",
                column: "booking_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_guest_reviews_host_id",
                table: "guest_reviews",
                column: "host_id");

            migrationBuilder.CreateIndex(
                name: "ix_stay_reviews_booking_id",
                table: "stay_reviews",
                column: "booking_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stay_reviews_host_id",
                table: "stay_reviews",
                column: "host_id");

            migrationBuilder.CreateIndex(
                name: "ix_stay_reviews_property_id",
                table: "stay_reviews",
                column: "property_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guest_reviews");

            migrationBuilder.DropTable(
                name: "stay_reviews");
        }
    }
}
