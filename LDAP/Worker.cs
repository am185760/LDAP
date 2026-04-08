using LDAP.Models;
using LDAP.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LDAP;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;

    public Worker(ILogger<Worker> logger, IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogWarning("LDAP Sync Worker Service is starting.");

        // Read value directly from config file
        double intervalHours = _configuration.GetValue<double>("SyncSettings:IntervalHours", 5);
        if (intervalHours <= 0) intervalHours = 5;

        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));

        // Perform immediate sync on start
        await DoSyncWork(stoppingToken);

        // Repeat every X hours using PeriodicTimer 
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await DoSyncWork(stoppingToken);
        }
        
        _logger.LogWarning("LDAP Sync Worker Service is stopping.");
    }


    private async Task DoSyncWork(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogWarning("Worker running at: {time}", DateTimeOffset.Now);

            // Create a scope to resolve scoped services like DbContext and LdapSyncService
            using var scope = _serviceProvider.CreateScope();
            var syncService = scope.ServiceProvider.GetRequiredService<LdapSyncService>();

            await syncService.SyncAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during execution of SyncWork.");
        }
    }
}
