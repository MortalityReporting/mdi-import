using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MortalityReporting.ImportApi.Configuration;
using Xunit;

namespace MortalityReporting.ImportApi.Tests;

[Collection("CORS configuration tests")]
public sealed class CorsBehaviorTests : IClassFixture<CorsWebApplicationFactory>
{
    private const string AllowedOrigin = "https://allowed-origin.com";
    private readonly CorsWebApplicationFactory _factory;

    public CorsBehaviorTests(CorsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AllowedOrigin_PreflightRequest_ReturnsCorsHeaders()
    {
        Assert.Equal(new[] { AllowedOrigin, "https://another-allowed-origin.org" },
            _factory.Services.GetRequiredService<ImportApiOptions>().CorsAllowedOrigins);
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Options, "/import");
        request.Headers.Add("Origin", AllowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out IEnumerable<string>? allowedOrigins),
            $"Status: {response.StatusCode}; Headers: {string.Join("; ", response.Headers)}");
        Assert.Equal(AllowedOrigin, allowedOrigins.Single());
        Assert.Contains("POST", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
        string allowedHeaders = response.Headers.GetValues("Access-Control-Allow-Headers").Single();
        Assert.Contains("authorization", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content-type", allowedHeaders, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllowedOrigin_ActualPostRequest_ReturnsCorsHeaders()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "/import")
        {
            Content = new StringContent("not multipart")
        };
        request.Headers.Add("Origin", AllowedOrigin);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AllowedOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task DisallowedOrigin_PreflightRequest_DoesNotReturnCorsHeaders()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Options, "/import");
        request.Headers.Add("Origin", "https://disallowed-origin.com");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task DisallowedOrigin_ActualPostRequest_DoesNotReturnCorsHeaders()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "/import")
        {
            Content = new StringContent("not multipart")
        };
        request.Headers.Add("Origin", "https://disallowed-origin.com");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}

public sealed class CorsWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        string? previousCorsAllowedOrigins = Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS");
        Environment.SetEnvironmentVariable("CORS_ALLOWED_ORIGINS", "https://allowed-origin.com,https://another-allowed-origin.org");
        try
        {
            return base.CreateHost(builder);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CORS_ALLOWED_ORIGINS", previousCorsAllowedOrigins);
        }
    }
}

[Collection("CORS configuration tests")]
public sealed class DefaultCorsBehaviorTests : IClassFixture<DefaultCorsWebApplicationFactory>
{
    private readonly DefaultCorsWebApplicationFactory _factory;

    public DefaultCorsBehaviorTests(DefaultCorsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task NoConfiguredOrigins_AllowsAnyOriginByDefault()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Options, "/import");
        request.Headers.Add("Origin", "https://any-origin.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }
}

public sealed class DefaultCorsWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        string? previousCorsAllowedOrigins = Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS");
        Environment.SetEnvironmentVariable("CORS_ALLOWED_ORIGINS", null);
        try
        {
            return base.CreateHost(builder);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CORS_ALLOWED_ORIGINS", previousCorsAllowedOrigins);
        }
    }
}

[CollectionDefinition("CORS configuration tests", DisableParallelization = true)]
public sealed class CorsTestCollectionDefinition;
