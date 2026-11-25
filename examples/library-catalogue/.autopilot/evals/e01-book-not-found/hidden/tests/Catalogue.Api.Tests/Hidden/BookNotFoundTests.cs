using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Catalogue.Api.Tests.Hidden;

// Hidden acceptance tests for work item 001. The agent never sees this file.
public sealed class BookNotFoundTests
{
    [Theory]
    [InlineData(42)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Unknown_book_returns_404_with_problem_details(int id)
    {
        await using var app = new WebApplicationFactory<Program>();
        var client = app.CreateClient();

        var response = await client.GetAsync($"/books/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, body.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Known_book_still_returns_200()
    {
        await using var app = new WebApplicationFactory<Program>();
        var client = app.CreateClient();

        var response = await client.GetAsync("/books/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("The Hobbit", body.RootElement.GetProperty("title").GetString());
    }
}
