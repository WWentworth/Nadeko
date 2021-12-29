#nullable disable
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using NadekoBot.Common.Configs;
using NadekoBot.Common.ModuleBehaviors;
using NadekoBot.Db;
using NadekoBot.Modules.Administration.Services;
using NadekoBot.Services.Database.Models;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using RunMode = Discord.Commands.RunMode;

namespace NadekoBot;

public sealed class Bot
{
    public event Func<GuildConfig, Task> JoinedGuild = delegate { return Task.CompletedTask; };

    public DiscordSocketClient Client { get; }
    public ImmutableArray<GuildConfig> AllGuildConfigs { get; private set; }

    private IServiceProvider Services { get; set; }

    public string Mention { get; private set; }
    public bool IsReady { get; private set; }
    private readonly IBotCredentials _creds;
    private readonly CommandService _commandService;
    private readonly DbService _db;
    private readonly IBotCredsProvider _credsProvider;
    private readonly InteractionService _interactionService;

    public Bot(int shardId, int? totalShards)
    {
        if (shardId < 0)
            throw new ArgumentOutOfRangeException(nameof(shardId));

        _credsProvider = new BotCredsProvider(totalShards);
        _creds = _credsProvider.GetCreds();

        _db = new(_creds);

        if (shardId == 0) _db.Setup();

        Client = new(new()
        {
            MessageCacheSize = 50,
            LogLevel = LogSeverity.Warning,
            ConnectionTimeout = int.MaxValue,
            TotalShards = _creds.TotalShards,
            ShardId = shardId,
            AlwaysDownloadUsers = false,
            AlwaysResolveStickers = false,
            AlwaysDownloadDefaultStickers = false,
            GatewayIntents = GatewayIntents.All
        });

        _commandService = new(new() { CaseSensitiveCommands = false, DefaultRunMode = RunMode.Sync });

        _interactionService = new(Client.Rest);

#if GLOBAL_NADEKO || DEBUG
        Client.Log += Client_Log;
#endif
    }

    public List<ulong> GetCurrentGuildIds()
        => Client.Guilds.Select(x => x.Id).ToList();

    private void AddServices()
    {
        var startingGuildIdList = GetCurrentGuildIds();
        var sw = Stopwatch.StartNew();
        var bot = Client.CurrentUser;

        using (var uow = _db.GetDbContext())
        {
            uow.EnsureUserCreated(bot.Id, bot.Username, bot.Discriminator, bot.AvatarId);
            AllGuildConfigs = uow.GuildConfigs.GetAllGuildConfigs(startingGuildIdList).ToImmutableArray();
        }

        var svcs = new ServiceCollection().AddTransient(_ => _credsProvider.GetCreds()) // bot creds
                                          .AddSingleton(_credsProvider)
                                          .AddSingleton(_db) // database
                                          .AddRedis(_creds.RedisOptions) // redis
                                          .AddSingleton(Client) // discord socket client
                                          .AddSingleton(_commandService)
                                          .AddSingleton(_interactionService)
                                          .AddSingleton(this)
                                          .AddSingleton<ISeria, JsonSeria>()
                                          .AddSingleton<IPubSub, RedisPubSub>()
                                          .AddSingleton<IConfigSeria, YamlSeria>()
                                          .AddBotStringsServices(_creds.TotalShards)
                                          .AddConfigServices()
                                          .AddConfigMigrators()
                                          .AddMemoryCache()
                                          // music
                                          .AddMusic();
                                          // admin
#if GLOBAL_NADEKO
        svcs.AddSingleton<ILogCommandService, DummyLogCommandService>();
#else
        svcs.AddSingleton<ILogCommandService, LogCommandService>();
#endif

        svcs.AddHttpClient();
        svcs.AddHttpClient("memelist")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        if (Environment.GetEnvironmentVariable("NADEKOBOT_IS_COORDINATED") != "1")
            svcs.AddSingleton<ICoordinator, SingleProcessCoordinator>();
        else
            svcs.AddSingleton<RemoteGrpcCoordinator>()
                .AddSingleton<ICoordinator>(x => x.GetRequiredService<RemoteGrpcCoordinator>())
                .AddSingleton<IReadyExecutor>(x => x.GetRequiredService<RemoteGrpcCoordinator>());

        svcs.AddSingleton<RedisLocalDataCache>()
            .AddSingleton<ILocalDataCache>(x => x.GetRequiredService<RedisLocalDataCache>())
            .AddSingleton<RedisImagesCache>()
            .AddSingleton<IImageCache>(x => x.GetRequiredService<RedisImagesCache>())
            .AddSingleton<IReadyExecutor>(x => x.GetRequiredService<RedisImagesCache>())
            .AddSingleton<IDataCache, RedisCache>();

        svcs.Scan(scan => scan.FromAssemblyOf<IReadyExecutor>()
                              .AddClasses(classes => classes.AssignableToAny(
                                      // services
                                      typeof(INService),

                                      // behaviours
                                      typeof(IEarlyBehavior),
                                      typeof(ILateBlocker),
                                      typeof(IInputTransformer),
                                      typeof(ILateExecutor))
#if GLOBAL_NADEKO
                    .WithoutAttribute<NoPublicBotAttribute>()
#endif
                              )
                              .AsSelfWithInterfaces()
                              .WithSingletonLifetime());

        //initialize Services
        Services = svcs.BuildServiceProvider();
        var exec = Services.GetRequiredService<IBehaviourExecutor>();
        exec.Initialize();

        if (Client.ShardId == 0) ApplyConfigMigrations();

        _ = LoadTypeReaders(typeof(Bot).Assembly);

        sw.Stop();
        Log.Information("All services loaded in {ServiceLoadTime:F2}s", sw.Elapsed.TotalSeconds);
    }

    private void ApplyConfigMigrations()
    {
        // execute all migrators
        var migrators = Services.GetServices<IConfigMigrator>();
        foreach (var migrator in migrators) migrator.EnsureMigrated();
    }

    private IEnumerable<object> LoadTypeReaders(Assembly assembly)
    {
        Type[] allTypes;
        try
        {
            allTypes = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            Log.Warning(ex.LoaderExceptions[0], "Error getting types");
            return Enumerable.Empty<object>();
        }

        var filteredTypes = allTypes.Where(x => x.IsSubclassOf(typeof(TypeReader))
                                                && x.BaseType?.GetGenericArguments().Length > 0
                                                && !x.IsAbstract);

        var toReturn = new List<object>();
        foreach (var ft in filteredTypes)
        {
            var x = (TypeReader)ActivatorUtilities.CreateInstance(Services, ft);
            var baseType = ft.BaseType;
            if (baseType is null)
                continue;
            var typeArgs = baseType.GetGenericArguments();
            _commandService.AddTypeReader(typeArgs[0], x);
            toReturn.Add(x);
        }

        return toReturn;
    }

    private async Task LoginAsync(string token)
    {
        var clientReady = new TaskCompletionSource<bool>();

        Task SetClientReady()
        {
            var _ = Task.Run(async () =>
            {
                clientReady.TrySetResult(true);
                try
                {
                    foreach (var chan in await Client.GetDMChannelsAsync()) await chan.CloseAsync();
                }
                catch
                {
                    // ignored
                }
            });
            return Task.CompletedTask;
        }

        //connect
        Log.Information("Shard {ShardId} logging in ...", Client.ShardId);
        try
        {
            await Client.LoginAsync(TokenType.Bot, token);
            await Client.StartAsync();
        }
        catch (HttpException ex)
        {
            LoginErrorHandler.Handle(ex);
            Helpers.ReadErrorAndExit(3);
        }
        catch (Exception ex)
        {
            LoginErrorHandler.Handle(ex);
            Helpers.ReadErrorAndExit(4);
        }

        Client.Ready += SetClientReady;
        await clientReady.Task;
        Client.Ready -= SetClientReady;

        Client.JoinedGuild += Client_JoinedGuild;
        Client.LeftGuild += Client_LeftGuild;

        Log.Information("Shard {ShardId} logged in", Client.ShardId);
    }

    private Task Client_LeftGuild(SocketGuild arg)
    {
        Log.Information("Left server: {GuildName} [{GuildId}]", arg?.Name, arg?.Id);
        return Task.CompletedTask;
    }

    private Task Client_JoinedGuild(SocketGuild arg)
    {
        Log.Information("Joined server: {GuildName} [{GuildId}]", arg.Name, arg.Id);
        var _ = Task.Run(async () =>
        {
            GuildConfig gc;
            await using (var uow = _db.GetDbContext())
            {
                gc = uow.GuildConfigsForId(arg.Id, null);
            }

            await JoinedGuild.Invoke(gc);
        });
        return Task.CompletedTask;
    }

    public async Task RunAsync()
    {
        var sw = Stopwatch.StartNew();

        await LoginAsync(_creds.Token);

        Mention = Client.CurrentUser.Mention;
        Log.Information("Shard {ShardId} loading services...", Client.ShardId);
        try
        {
            AddServices();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error adding services");
            Helpers.ReadErrorAndExit(9);
        }

        sw.Stop();
        Log.Information("Shard {ShardId} connected in {Elapsed:F2}s", Client.ShardId, sw.Elapsed.TotalSeconds);
        var commandHandler = Services.GetRequiredService<CommandHandler>();

        // start handling messages received in commandhandler
        await commandHandler.StartHandling();

        await _commandService.AddModulesAsync(typeof(Bot).Assembly, Services);
        await _interactionService.AddModulesAsync(typeof(Bot).Assembly, Services);
        await _interactionService.RegisterCommandsToGuildAsync(117523346618318850);
        IsReady = true;
        _ = Task.Run(ExecuteReadySubscriptions);
        Log.Information("Shard {ShardId} ready", Client.ShardId);
    }

    private Task ExecuteReadySubscriptions()
    {
        var readyExecutors = Services.GetServices<IReadyExecutor>();
        var tasks = readyExecutors.Select(async toExec =>
        {
            try
            {
                await toExec.OnReadyAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex,
                    "Failed running OnReadyAsync method on {Type} type: {Message}",
                    toExec.GetType().Name,
                    ex.Message);
            }
        });

        return tasks.WhenAll();
    }

    private Task Client_Log(LogMessage arg)
    {
        if (arg.Exception != null)
            Log.Warning(arg.Exception, "{ErrorSource} | {ErrorMessage}", arg.Source, arg.Message);
        else
            Log.Warning("{ErrorSource} | {ErrorMessage}", arg.Source, arg.Message);

        return Task.CompletedTask;
    }

    public async Task RunAndBlockAsync()
    {
        await RunAsync();
        await Task.Delay(-1);
    }
}