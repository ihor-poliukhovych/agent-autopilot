using System.Net;
using System.Net.Http.Json;
using Catalogue.Api.Store;

namespace Catalogue.Api.Tests;

public sealed class MemberTests
{
    [Fact]
    public async Task Get_by_id_returns_the_member()
    {
        await using var app = new CatalogueApp();
        var client = app.CreateClient();

        var member = await client.GetFromJsonAsync<Member>("/members/3");

        Assert.Equal(new Member(3, "Grace Hopper"), member);
    }

    [Fact]
    public async Task Unknown_member_returns_404_with_problem_details()
    {
        await using var app = new CatalogueApp();
        var client = app.CreateClient();

        var response = await client.GetAsync("/members/99");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
