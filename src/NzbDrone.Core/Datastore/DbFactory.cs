using System;
using System.Data.Common;
using System.Data.SQLite;
using System.Net.Sockets;
using NLog;
using Npgsql;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Exceptions;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore
{
    public interface IDbFactory
    {
        IDatabase Create(MigrationType migrationType = MigrationType.Main);
        IDatabase Create(MigrationContext migrationContext);
    }

    public class DbFactory : IDbFactory
    {
        private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(DbFactory));
        private readonly IMigrationController _migrationController;
        private readonly IConnectionStringFactory _connectionStringFactory;
        private readonly IDiskProvider _diskProvider;
        private readonly IRestoreDatabase _restoreDatabaseService;

        static DbFactory()
        {
            InitializeEnvironment();

            TableMapping.Map();
        }

        private static void InitializeEnvironment()
        {
            // Speed up sqlite3 initialization since we don't use the config file and can't rely on preloading.
            Environment.SetEnvironmentVariable("No_Expand", "true");
            Environment.SetEnvironmentVariable("No_SQLiteXmlConfigFile", "true");
            Environment.SetEnvironmentVariable("No_PreLoadSQLite", "true");
            Environment.SetEnvironmentVariable("No_SQLiteFunctions", "true");
        }

        public DbFactory(IMigrationController migrationController,
                         IConnectionStringFactory connectionStringFactory,
                         IDiskProvider diskProvider,
                         IRestoreDatabase restoreDatabaseService)
        {
            _migrationController = migrationController;
            _connectionStringFactory = connectionStringFactory;
            _diskProvider = diskProvider;
            _restoreDatabaseService = restoreDatabaseService;
        }

        public IDatabase Create(MigrationType migrationType = MigrationType.Main)
        {
            return Create(new MigrationContext(migrationType));
        }

        public IDatabase Create(MigrationContext migrationContext)
        {
            string connectionString;

            switch (migrationContext.MigrationType)
            {
                case MigrationType.Main:
                    {
                        connectionString = _connectionStringFactory.MainDbConnectionString;
                        CreateMain(connectionString, migrationContext);

                        break;
                    }

                case MigrationType.Log:
                    {
                        connectionString = _connectionStringFactory.LogDbConnectionString;
                        CreateLog(connectionString, migrationContext);

                        break;
                    }

                default:
                    {
                        throw new ArgumentException("Invalid MigrationType");
                    }
            }

            var db = new Database(migrationContext.MigrationType.ToString(), () =>
            {
                DbConnection conn;

                if (connectionString.Contains(".db"))
                {
                    conn = SQLiteFactory.Instance.CreateConnection();
                    conn.ConnectionString = connectionString;
                }
                else
                {
                    conn = new NpgsqlConnection(connectionString);
                }

                conn.Open();
                return conn;
            });

            return db;
        }

        private void CreateMain(string connectionString, MigrationContext migrationContext)
        {
            try
            {
                _restoreDatabaseService.Restore();
                _migrationController.Migrate(connectionString, migrationContext);
            }
            catch (SQLiteException e)
            {
                var fileName = _connectionStringFactory.GetDatabasePath(connectionString);

                if (OsInfo.IsOsx)
                {
                    throw new CorruptDatabaseException("Database file: {0} is corrupt, restore from backup if available. See: https://wiki.servarr.com/radarr/faq#i-use-radarr-on-a-mac-and-it-suddenly-stopped-working-what-happened", e, fileName);
                }

                throw new CorruptDatabaseException("Database file: {0} is corrupt, restore from backup if available. See: https://wiki.servarr.com/radarr/faq#i-am-getting-an-error-database-disk-image-is-malformed", e, fileName);
            }
            catch (NpgsqlException e)
            {
                if (e.InnerException is SocketException)
                {
                    var retryCount = 3;

                    while (true)
                    {
                        Logger.Error(e, "Failure to connect to Postgres DB, {0} retries remaining", retryCount);

                        try
                        {
                            _migrationController.Migrate(connectionString, migrationContext);
                        }
                        catch (Exception ex)
                        {
                            if (--retryCount > 0)
                            {
                                System.Threading.Thread.Sleep(5000);
                                continue;
                            }

                            throw new RadarrStartupException(ex, "Error creating main database");
                        }
                    }
                }
                else
                {
                    throw new RadarrStartupException(e, "Error creating main database");
                }
            }
            catch (Exception e)
            {
                throw new RadarrStartupException(e, "Error creating main database");
            }
        }

        private void CreateLog(string connectionString, MigrationContext migrationContext)
        {
            try
            {
                _migrationController.Migrate(connectionString, migrationContext);
            }
            catch (SQLiteException e)
            {
                var fileName = _connectionStringFactory.GetDatabasePath(connectionString);

                Logger.Error(e, "Logging database is corrupt, attempting to recreate it automatically");

                try
                {
                    _diskProvider.DeleteFile(fileName + "-shm");
                    _diskProvider.DeleteFile(fileName + "-wal");
                    _diskProvider.DeleteFile(fileName + "-journal");
                    _diskProvider.DeleteFile(fileName);
                }
                catch (Exception)
                {
                    Logger.Error("Unable to recreate logging database automatically. It will need to be removed manually.");
                }

                _migrationController.Migrate(connectionString, migrationContext);
            }
            catch (Exception e)
            {
                throw new RadarrStartupException(e, "Error creating log database");
            }
        }
    }
}
