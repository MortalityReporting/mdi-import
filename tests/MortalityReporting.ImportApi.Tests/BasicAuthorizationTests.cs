using Microsoft.AspNetCore.Http;
using MortalityReporting.ImportApi.Configuration;
using Xunit;

namespace MortalityReporting.ImportApi.Tests;

public sealed class BasicAuthorizationTests
{
    [Fact]
    public void AcceptsCorrectBasicCredentials()
    {
        DefaultHttpContext context = new();
        context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("api-user:api-pass"));
        ImportApiOptions options = new() { IncomingUsername = "api-user", IncomingPassword = "api-pass" };
        Assert.True(BasicAuthorization.IsValid(context.Request, options));
    }

    [Fact]
    public void RejectsIncorrectBasicCredentials()
    {
        DefaultHttpContext context = new();
        context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("api-user:wrong"));
        ImportApiOptions options = new() { IncomingUsername = "api-user", IncomingPassword = "api-pass" };
        Assert.False(BasicAuthorization.IsValid(context.Request, options));
    }
}
