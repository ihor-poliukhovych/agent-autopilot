using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Catalogue.Api.Tests.Hidden;

// Hidden acceptance tests for work item 002. The agent never sees this file.
public sealed class AuthorSearchTests
{
    [Theory]
    [InlineData("tolkien")]
    [InlineData("TOLKIEN")]
    [InlineData("R. R. Tolk")]
    public async Task Author_filter_matches_part_of_the_name_ignoring_case(string author)
    {
        var titles = await TitlesAsync($"/books?author={Uri.EscapeDataString(author)}");

        Assert.Equal(["The Hobbit", "The Fellowship of the Ring"], titles);
    }

    [Fact]
    public async Task Author_filter_keeps_id_order()
    {
        var titles = await TitlesAsync("/books?author=le%20guin");

        Assert.Equal(["A Wizard of Earthsea", "The Left Hand of Darkness"], titles);
    }

    [Fact]
    public async Task No_match_returns_an_empty_list()
    {
        await using var app = new WebApplicationFactory<Program>();
        var client = app.CreateClient();

        var response = await client.GetAsync("/books?author=Nobody%20Here");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        Assert.Equal(0, body.RootElement.GetArrayLength());
    }

    [Theory]
    [InlineData("/books")]
    [InlineData("/books?author=")]
    public async Task Without_a_filter_every_book_is_listed(string url)
    {
        var titles = await TitlesAsync(url);

        Assert.Equal(6, titles.Count);
    }

    private static async Task<List<string>> TitlesAsync(string url)
    {
        await using var app = new WebApplicationFactory<Program>();
        var client = app.CreateClient();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.EnumerateArray().Select(b => b.GetProperty("title").GetString()!).ToList();
    }
}
