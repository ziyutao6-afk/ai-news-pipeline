using System.Net;
using System.Text.Json;
using AutoTweetRss.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Functions;

public sealed class TwitterHealthFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly ILogger<TwitterHealthFunction> _logger;
    private readonly TwitterApiClient _twitterApiClient;

    public TwitterHealthFunction(
        ILogger<TwitterHealthFunction> logger,
        TwitterApiClient twitterApiClient)
    {
        _logger = logger;
        _twitterApiClient = twitterApiClient;
    }

    [Function("TwitterHealth")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "twitter/health")] HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var health = await _twitterApiClient.CheckHealthAsync(timeout.Token);

            var body = new
            {
                xApiEnabled = health.XApiEnabled,
                twitterConfigured = health.TwitterConfigured,
                oauthWorking = health.OauthWorking,
                readAndWriteAvailable = health.ReadAndWriteAvailable,
                mediaUploadAvailable = health.MediaUploadAvailable,
                canPublish = health.CanPublish,
                username = health.Username,
                userId = health.UserId,
                credentials = new
                {
                    apiKeyPresent = health.ApiKeyPresent,
                    apiSecretPresent = health.ApiSecretPresent,
                    accessTokenPresent = health.AccessTokenPresent,
                    accessSecretPresent = health.AccessSecretPresent
                },
                mediaHealthCheckMediaId = health.MediaHealthCheckMediaId,
                errors = health.Errors,
                warnings = health.Warnings
            };

            await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Twitter health check failed");

            response.StatusCode = HttpStatusCode.InternalServerError;
            await response.WriteStringAsync(JsonSerializer.Serialize(new
            {
                twitterConfigured = _twitterApiClient.IsConfigured,
                oauthWorking = false,
                mediaUploadAvailable = false,
                canPublish = false,
                username = string.Empty,
                userId = string.Empty,
                error = ex.Message,
                stackTrace = ex.ToString()
            }, JsonOptions));
            return response;
        }
    }
}
