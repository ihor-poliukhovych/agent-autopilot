using System.Net;
using System.Net.Http.Json;
using Catalogue.Api.Store;

namespace Catalogue.Api.Tests;

public sealed class LoanTests
{
    [Fact]
    public async Task Borrowing_creates_a_loan_due_in_fourteen_days()
    {
        await using var app = new CatalogueApp();
        var client = app.CreateClient();

        var response = await app.BorrowAsync(client, bookId: 1, memberId: 1);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var loan = await response.Content.ReadFromJsonAsync<Loan>();
        Assert.NotNull(loan);
        Assert.Equal(1, loan.BookId);
        Assert.Equal(1, loan.MemberId);
        Assert.Null(loan.ReturnedAt);
        Assert.Equal(TimeSpan.FromDays(14), loan.DueAt - loan.LoanedAt);
        Assert.Equal($"/loans/{loan.Id}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Borrowing_an_unknown_book_returns_404()
    {
        await using var app = new CatalogueApp();
        var client = app.CreateClient();

        var response = await app.BorrowAsync(client, bookId: 99, memberId: 1);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Borrowing_for_an_unknown_member_returns_404()
    {
        await using var app = new CatalogueApp();
        var client = app.CreateClient();

        var response = await app.BorrowAsync(client, bookId: 1, memberId: 99);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Borrowing_is_refused_when_every_copy_is_on_loan()
    {
        await using var app = new CatalogueApp();
        var client = app.CreateClient();

        // Neuromancer has a single copy.
        var first = await app.BorrowAsync(client, bookId: 6, memberId: 1);
        var second = await app.BorrowAsync(client, bookId: 6, memberId: 2);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task A_returned_copy_can_be_borrowed_again()
    {
        await using var app = new CatalogueApp();
        var client = app.CreateClient();

        var first = await (await app.BorrowAsync(client, bookId: 6, memberId: 1)).Content.ReadFromJsonAsync<Loan>();
        var returned = await client.PostAsync($"/loans/{first!.Id}/return", content: null);
        var again = await app.BorrowAsync(client, bookId: 6, memberId: 2);

        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [Fact]
    public async Task Returning_sets_returned_at()
    {
        await using var app = new CatalogueApp();
        var client = app.CreateClient();

        var loan = await (await app.BorrowAsync(client, bookId: 3, memberId: 2)).Content.ReadFromJsonAsync<Loan>();
        var response = await client.PostAsync($"/loans/{loan!.Id}/return", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var returned = await response.Content.ReadFromJsonAsync<Loan>();
        Assert.NotNull(returned);
        Assert.NotNull(returned.ReturnedAt);
        Assert.True(returned.ReturnedAt >= returned.LoanedAt);
    }

    [Fact]
    public async Task Returning_an_unknown_loan_returns_404()
    {
        await using var app = new CatalogueApp();
        var client = app.CreateClient();

        var response = await client.PostAsync("/loans/99/return", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
