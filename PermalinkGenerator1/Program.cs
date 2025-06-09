using EntityFramework.Exceptions.PostgreSQL;
using FluentStorage;
using FluentStorage.Blobs;
using FM.Cinema.Core.Domain.Configurations;
using FM.Cinema.Core.Domain.Constants;
using FM.Cinema.Core.Domain.Services;
using FM.Cinema.Core.Infrastructure.Persistence;
using FM.Cinema.Core.Infrastructure.Persistence.Configurations;
using FM.Cinema.Core.Infrastructure.Persistence.Interceptors;
using FM.Cinema.Core.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Minio;
using Npgsql;
using PermalinkGenerator1.Services;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using System.Globalization;

namespace PermalinkGenerator1;

public class Program
{
    public static async Task Main(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        environment = string.IsNullOrEmpty(environment)
            ? "Local"
            : environment;

        var currentAppSettings = $"appsettings.{environment}.json";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)

            .AddJsonFile(currentAppSettings, false)
            .Build();

        var services = new ServiceCollection();



        Serilog.ILogger logger = new LoggerConfiguration()
            .WriteTo.Async(
                c => c.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                    formatProvider: CultureInfo.InvariantCulture
                )
            )

            .MinimumLevel
            .Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .CreateLogger();

        Microsoft.Extensions.Logging.ILogger microsoftLogger =
            new SerilogLoggerFactory(logger).CreateLogger<Program>();

        services.AddLogging(builder =>
            builder.ClearProviders()
                .AddSerilog(logger)
                .SetMinimumLevel(LogLevel.Information));

        services.AddSingleton(microsoftLogger);

        AddDbContext<ApplicationDbContext>(services, configuration);
        AddMinioService(services, configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ContentPermalinkUpdater>();

        var serviceProvider = services.BuildServiceProvider();

        var contentPermalinkUpdater = serviceProvider.GetRequiredService<ContentPermalinkUpdater>();

        await contentPermalinkUpdater.UpdatePermalinksAsync(CancellationToken.None);

        Console.WriteLine("Done!");
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }

    private static void AddDbContext<T>(ServiceCollection services, IConfigurationRoot configuration)
        where T : DbContext
    {
        services.AddScoped<AuditableEntityInterceptor>();

        var connectionString = configuration.GetConnectionString(nameof(ApplicationDbContext));

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);

        dataSourceBuilder.AddKinopoiskEnumsMap();
        dataSourceBuilder.AddEnumsMap();
        dataSourceBuilder.AddEncodeEnumsMap();

        dataSourceBuilder.UseVector();
        dataSourceBuilder.UseNodaTime();

        var dataSource = dataSourceBuilder.Build();

        services.AddDbContext<T>((sp, options) =>
        {
            options.UseNpgsql(dataSource, o =>
            {
                o.UseVector();
                o.UseNodaTime();
            });

            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
            options.UseSnakeCaseNamingConvention();
            options.UseExceptionProcessor();

            options.ConfigureWarnings(cw =>
            {
                cw.Ignore(
                    new EventId(10400), // Infrastructure.SensitiveDataLoggingEnabledWarning
                    new EventId(10622), // PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning
                    new EventId(20504) // Query.MultipleCollectionIncludeWarning
                );
            });

            options.AddInterceptors(sp.GetRequiredService<AuditableEntityInterceptor>());
        });
    }


    private static void AddMinioService(ServiceCollection services, IConfigurationRoot configuration)
    {
        var options = configuration.GetSection(nameof(FileConfiguration)).Get<FileConfiguration>();

        if (options is null)
            return;

        var uri = new Uri(options.ConnectionString);

        services.AddMinio(provider => provider
            .WithEndpoint(uri.Host, uri.Port)
            .WithCredentials(options.AccessKey, options.SecretKey)
            .WithSSL(false)
            .Build());

        services.AddKeyedSingleton(DependencyInjectionKeyConstants.FilesStorage,
            GetBlobStorage(options.FilesBucket, options));
        services.AddKeyedSingleton(DependencyInjectionKeyConstants.ContentsStorage,
            GetBlobStorage(options.ContentsBucket, options));

        services.AddScoped<IStorageService, StorageService>();
        services.AddScoped<IAvatarsService, AvatarsService>();
    }

    private static IBlobStorage GetBlobStorage(string bucketName, FileConfiguration options)
    {
        return StorageFactory.Blobs.MinIO(
            accessKeyId: options.AccessKey,
            secretAccessKey: options.SecretKey,
            bucketName: bucketName,
            awsRegion: null,
            minioServerUrl: options.ConnectionString
        );
    }
}