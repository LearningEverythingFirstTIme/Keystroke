using System.Net;
using System.Net.Http;
using System.Text;
using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class GeminiApiKeyValidationServiceTests
{
    [Fact]
    public async Task ValidateAsync_ReturnsMissingForBlankKey()
    {
        using var service = new GeminiApiKeyValidationService(new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException())), ownsClient: true);

        var result = await service.ValidateAsync("");

        Assert.Equal(GeminiApiKeyValidationStatus.Missing, result.Status);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsValidForSuccess()
    {
        using var service = new GeminiApiKeyValidationService(new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"OK\"}]}}]}", Encoding.UTF8, "application/json")
            }))), ownsClient: true);

        var result = await service.ValidateAsync("valid-gemini-key-value-12345");

        Assert.Equal(GeminiApiKeyValidationStatus.Valid, result.Status);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsUnauthorizedForRejectedKey()
    {
        using var service = new GeminiApiKeyValidationService(new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("API key not valid", Encoding.UTF8, "text/plain")
            }))), ownsClient: true);

        var result = await service.ValidateAsync("bad-gemini-key-value-12345");

        Assert.Equal(GeminiApiKeyValidationStatus.Unauthorized, result.Status);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsQuotaLimitedForQuotaErrors()
    {
        using var service = new GeminiApiKeyValidationService(new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("quota exceeded", Encoding.UTF8, "text/plain")
            }))), ownsClient: true);

        var result = await service.ValidateAsync("quota-gemini-key-value-12345");

        Assert.Equal(GeminiApiKeyValidationStatus.QuotaLimited, result.Status);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsNetworkErrorForHttpFailures()
    {
        using var service = new GeminiApiKeyValidationService(new HttpClient(new StubHttpMessageHandler(_ =>
            throw new HttpRequestException("network down"))), ownsClient: true);

        var result = await service.ValidateAsync("network-gemini-key-value-12345");

        Assert.Equal(GeminiApiKeyValidationStatus.NetworkError, result.Status);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request);
    }
}
