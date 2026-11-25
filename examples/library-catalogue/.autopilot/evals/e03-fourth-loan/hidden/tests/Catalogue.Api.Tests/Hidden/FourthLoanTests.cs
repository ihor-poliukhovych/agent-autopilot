using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Catalogue.Api.Tests.Hidden;

// Hidden acceptance tests for work item 003. The agent never sees this file.
public sealed class FourthLoanTests
{
    [Fact]
    public async Task Fourth_active_loan_is_refused_with_409()
    {
        await using var app = new WebApplicationFactory<Program>();
        var client = app.CreateClient();

        foreach (var bookId in new[] { 1, 3, 5 })
        {
            Assert.Equal(HttpStatusCode.Created, (await BorrowAsync(client, bookId, memberId: 1)).StatusCode);
        }

        var fourth = await BorrowAsync(client, bookId: 4, memberId: 1);

        Assert.Equal(HttpStatusCode.Conflict, fourth.StatusCode);
        Assert.Equal("application/problem+json", fourth.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task After_a_return_the_member_can_borrow_again()
    {
        await using var app = new WebApplicationFactory<Program>();
        var client = app.CreateClient();

        var loanIds = new List<int>();
        foreach (var bookId in new[] { 1, 3, 5 })
        {
            var response = await BorrowAsync(client, bookId, memberId: 2);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            loanIds.Add(body.RootElement.GetProperty("id").GetInt32());
        }

        Assert.Equal(HttpStatusCode.Conflict, (await BorrowAsync(client, bookId: 4, memberId: 2)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/loans/{loanIds[0]}/return", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await BorrowAsync(client, bookId: 4, memberId: 2)).StatusCode);
    }

    [Fact]
    public async Task The_limit_is_per_member()
    {
        await using var app = new WebApplicationFactory<Program>();
        var client = app.CreateClient();

        foreach (var bookId in new[] { 1, 3, 5 })
        {
            await BorrowAsync(client, bookId, memberId: 3);
        }

        Assert.Equal(HttpStatusCode.Created, (await BorrowAsync(client, bookId: 4, memberId: 1)).StatusCode);
    }

    private static Task<HttpResponseMessage> BorrowAsync(HttpClient client, int bookId, int memberId) =>
        client.PostAsJsonAsync("/loans", new { bookId, memberId });
}
