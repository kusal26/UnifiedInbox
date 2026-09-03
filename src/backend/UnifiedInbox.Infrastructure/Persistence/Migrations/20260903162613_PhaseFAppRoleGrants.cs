using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnifiedInbox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PhaseFAppRoleGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Least-privilege runtime role for the API and worker. The role itself is
            // created by db/init/01-app-role.sh (compose) or the DBA (managed Postgres);
            // grants apply only when the role exists so older environments keep migrating.
            migrationBuilder.Sql("""
                DO $grants$
                BEGIN
                  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'app_runtime') THEN
                    RAISE NOTICE 'app_runtime role is absent; skipping runtime grants';
                    RETURN;
                  END IF;
                  GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO app_runtime;
                  GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO app_runtime;
                  ALTER DEFAULT PRIVILEGES FOR ROLE unified_inbox IN SCHEMA public
                    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO app_runtime;
                  ALTER DEFAULT PRIVILEGES FOR ROLE unified_inbox IN SCHEMA public
                    GRANT USAGE, SELECT ON SEQUENCES TO app_runtime;
                END $grants$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $grants$
                BEGIN
                  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'app_runtime') THEN
                    REVOKE ALL ON ALL TABLES IN SCHEMA public FROM app_runtime;
                    REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM app_runtime;
                  END IF;
                END $grants$;
                """);
        }
    }
}
