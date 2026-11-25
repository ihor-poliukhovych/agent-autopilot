using System.Net;

namespace Catalogue.Api.Tests;

public sealed class LoanLimitTests
{
    [Fact]
    public async Task A_member_can_hold_three_books()
    {
        await using var app = new CatalogueApp();
        var client = app.CreateClient();

        foreach (var bookId in new[] { 1, 3, 5 })
        {
            var response = await app.BorrowAsync(client, bookId, memberId: 3);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    [Fact]
    public async Task Borrowing_far_past_the_limit_is_refused()
    {
        await using var app = new CatalogueApp();
        var client = app.CreateClient();

        HttpResponseMessage? last = null;
        foreach (var bookId in new[] { 1, 3, 4, 5, 5 })
        {
            last = await app.BorrowAsync(client, bookId, memberId: 3);
        }

        Assert.Equal(HttpStatusCode.Conflict, last!.StatusCode);
    }

    [Fact]
    public async Task Returning_a_book_frees_a_place_under_the_limit()
    {
        await using var app = new CatalogueApp();
        var client = app.CreateClient();

        foreach (var bookId in new[] { 1, 3, 5 })
        {
            await app.BorrowAsync(client, bookId, memberId: 2);
        }

        var returned = await client.PostAsync("/loans/1/return", content: null);
        var next = await app.BorrowAsync(client, bookId: 4, memberId: 2);

        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
    }
}
