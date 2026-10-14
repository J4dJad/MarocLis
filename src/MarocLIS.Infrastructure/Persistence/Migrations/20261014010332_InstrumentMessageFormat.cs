using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarocLIS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InstrumentMessageFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InstrumentFlags",
                schema: "res",
                table: "TestResults",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FormatAcceptedResultTypes",
                schema: "eqp",
                table: "InstrumentInterfaces",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FormatSpecimenIdComponent",
                schema: "eqp",
                table: "InstrumentInterfaces",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FormatSpecimenIdField",
                schema: "eqp",
                table: "InstrumentInterfaces",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FormatTestCodeComponent",
                schema: "eqp",
                table: "InstrumentInterfaces",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FormatTestCodeSuffix",
                schema: "eqp",
                table: "InstrumentInterfaces",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "KeepInstrumentFlags",
                schema: "eqp",
                table: "InstrumentInterfaces",
                type: "bit",
                nullable: false,
                // Existing links keep the analyser's flags: the safe choice, and the new default.
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "UnmappedNonNumeric",
                schema: "eqp",
                table: "InstrumentInterfaces",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InstrumentFlags",
                schema: "res",
                table: "TestResults");

            migrationBuilder.DropColumn(
                name: "FormatAcceptedResultTypes",
                schema: "eqp",
                table: "InstrumentInterfaces");

            migrationBuilder.DropColumn(
                name: "FormatSpecimenIdComponent",
                schema: "eqp",
                table: "InstrumentInterfaces");

            migrationBuilder.DropColumn(
                name: "FormatSpecimenIdField",
                schema: "eqp",
                table: "InstrumentInterfaces");

            migrationBuilder.DropColumn(
                name: "FormatTestCodeComponent",
                schema: "eqp",
                table: "InstrumentInterfaces");

            migrationBuilder.DropColumn(
                name: "FormatTestCodeSuffix",
                schema: "eqp",
                table: "InstrumentInterfaces");

            migrationBuilder.DropColumn(
                name: "KeepInstrumentFlags",
                schema: "eqp",
                table: "InstrumentInterfaces");

            migrationBuilder.DropColumn(
                name: "UnmappedNonNumeric",
                schema: "eqp",
                table: "InstrumentInterfaces");
        }
    }
}
