using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public sealed class TwitterStartupHealthLogger : IHostedService
{
    private readonly ILogger<TwitterStartupHealthLogger> _logger;
    private readonly TwitterApiClient _twitterApiClient;

    public TwitterStartupHealthLogger(
        ILogger<TwitterStartupHealthLogger> logger,
        TwitterApiClient twitterApiClient)
    {
        _logger = logger;
        _twitterApiClient = twitterApiClient;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            var health = await _twitterApiClient.CheckHealthAsync(timeout.Token);
            _logger.LogInformation("Twitter configured: {Configured}", health.TwitterConfigured);
            _logger.LogInformation("Twitter publish available: {PublishAvailable}", health.CanPublish);
            _logger.LogInformation("Twitter media upload available: {MediaUploadAvailable}", health.MediaUploadAvailable);
            _logger.LogInformation("Twitter username: {Username}", string.IsNullOrWhiteSpace(health.Username) ? "(unknown)" : health.Username);

            foreach (var error in health.Errors)
            {
                _logger.LogWarning("Twitter health error: {Error}", error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Twitter startup health check failed");
            _logger.LogInformation("Twitter configured: {Configured}", _twitterApiClient.IsConfigured);
            _logger.LogInformation("Twitter publish available: False");
            _logger.LogInformation("Twitter media upload available: False");
            _logger.LogInformation("Twitter username: (unknown)");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
