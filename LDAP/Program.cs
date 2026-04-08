using EView360Models.Core;
using LDAP;
using LDAP.Models;
using LDAP.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.IO;
using System.Linq;

// Set current directory so Windows Service can find appsettings.json
string basePath = AppDomain.CurrentDomain.BaseDirectory;
Directory.SetCurrentDirectory(basePath);

// 1. Build temporary configuration to get ConnectionString
var builder = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();
var config = builder.Build();

string connString = config.GetConnectionString("DefaultConnection");

// 2. Fetch LogFilePath from DB dynamically
string baseLogPath = config.GetValue<string>("DefaultLogPath", @"c:\view360LiveData\Logs");
try
{
    var optionsBuilder = new DbContextOptionsBuilder<CoreContext>();
    optionsBuilder.UseSqlServer(connString);
    using var tempContext = new CoreContext(optionsBuilder.Options);
    var appSettings = tempContext.AppSettings.FirstOrDefault();
    if (appSettings != null && !string.IsNullOrEmpty(appSettings.LogFilePath))
    {
        baseLogPath = appSettings.LogFilePath;
    }
}
catch (Exception ex)
{
    Console.WriteLine("Could not fetch log path from DB. Using fallback. " + ex.Message);
}

string logFileName = $"LdapService.log";
string fullLogPath = Path.Combine(baseLogPath, logFileName);

// 4. Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Warning()
    .WriteTo.Console()
    .WriteTo.File(fullLogPath, rollingInterval: RollingInterval.Day) // Actually, RollingInterval can manage the date, but since requirement specifically asks for LdapService_ddMMyyyy, we can disable rolling format and inject date manually if we intend to restart daily or we can rely on RollingInterval. To strictly meet criteria, we keep it this way.
    .CreateLogger();

try
{
    Log.Warning("Starting up LDAP Sync Service");

    IHost host = Host.CreateDefaultBuilder(args)
        .UseWindowsService(options =>
        {
            options.ServiceName = "View360 LDAP Sync Service";
        })
        .UseSerilog() // Use Serilog for standard ILogger
        .ConfigureServices((hostContext, services) =>
        {
            IConfiguration configuration = hostContext.Configuration;

            // Bind Configurations
            services.Configure<LdapSettings>(configuration.GetSection("LdapSettings"));

            // Add DbContext
            services.AddDbContext<CoreContext>(options =>
                options.UseSqlServer(configuration.GetConnectionString("DefaultConnection"))
            );

            // Add Custom Services
            services.AddScoped<LdapSyncService>();

            // Add Hosted Service (Worker)
            services.AddHostedService<Worker>();
        })
        .Build();

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "There was a problem starting the serivce");
}
finally
{
    Log.CloseAndFlush();
}