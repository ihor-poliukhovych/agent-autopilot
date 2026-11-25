using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Catalogue.Api.Tests.Hidden;

// Hidden acceptance tests for work item 004. The agent never sees this file.
public sealed class ValidateNewBookTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_title_is_rejected(string title)
    {
        await AssertRejectedAsync("9780553293357", title, field: "title");
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("978055329335")]
    [InlineData("97805532933570")]
    [InlineData("97805532933X7")]
    [InlineData("978 0553293357")]
    [InlineData("")]
    public async Task Isbn_that_is_not_10_or_13_digits_is_rejected(string isbn)
    {
        await AssertRejectedAsync(isbn, "Foundation", field: "isbn");
    }

    [Theory]
    [InlineData("9780553293357")]
    [InlineData("0553293354")]
    [InlineData("978-0-553-29335-7")]
    public async Task Valid_book_is_created(string isbn)
    {
        await using var app = new WebApplicationFactory<Program>();
        var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/books", NewBook(isbn, "Foundation"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(isbn, body.RootElement.GetProperty("isbn").GetString());
    }

    private static async Task AssertRejectedAsync(string isbn, string title, string field)
    {
        await using var app = new WebApplicationFactory<Program>();
        var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/books", NewBook(isbn, title));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = body.RootElement.GetProperty("errors");
        Assert.Contains(errors.EnumerateObject(), e => string.Equals(e.Name, field, StringComparison.OrdinalIgnoreCase));

        var books = JsonDocument.Parse(await client.GetStringAsync("/books"));
        Assert.Equal(6, books.RootElement.GetArrayLength());
    }

    private static object NewBook(string isbn, string title) =>
        new { isbn, title, author = "Isaac Asimov", year = 1951, copies = 2 };
}
