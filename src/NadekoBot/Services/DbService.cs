#nullable disable
using LinqToDB.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NadekoBot.Services.Database;

namespace NadekoBot.Services;

public class DbService
{
    private readonly DbContextOptions<NadekoContext> options;
    private readonly DbContextOptions<NadekoContext> migrateOptions;

    public DbService(IBotCredentials creds)
    {
        LinqToDBForEFTools.Initialize();

        var builder = new SqliteConnectionStringBuilder(creds.Db.ConnectionString);
        builder.DataSource = Path.Combine(AppContext.BaseDirectory, builder.DataSource);

        var optionsBuilder = new DbContextOptionsBuilder<NadekoContext>();
        optionsBuilder.UseSqlite(builder.ToString());
        options = optionsBuilder.Options;

        optionsBuilder = new();
        optionsBuilder.UseSqlite(builder.ToString());
        migrateOptions = optionsBuilder.Options;
    }

    public void Setup()
    {
        using var context = new NadekoContext(options);
        if (context.Database.GetPendingMigrations().Any())
        {
            var mContext = new NadekoContext(migrateOptions);
            mContext.Database.Migrate();
            mContext.SaveChanges();
            mContext.Dispose();
        }

        context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL");
        context.SaveChanges();
    }

    private NadekoContext GetDbContextInternal()
    {
        var context = new NadekoContext(options);
        context.Database.SetCommandTimeout(60);
        var conn = context.Database.GetDbConnection();
        conn.Open();
        using var com = conn.CreateCommand();
        com.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=OFF";
        com.ExecuteNonQuery();
        return context;
    }

    public NadekoContext GetDbContext()
        => GetDbContextInternal();
}