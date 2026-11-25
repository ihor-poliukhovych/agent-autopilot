using System.Net.Http.Json;
using Catalogue.Api.Store;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Catalogue.Api.Tests;

/// <summary>
/// The API in memory. Each test creates its own instance, so every test starts from the seeded
/// catalogue and no test sees another test's loans.
/// </summary>
public sealed class CatalogueApp : WebApplicationFactory<Program>
{
    public async Task<HttpResponseMessage> BorrowAsync(HttpClient client, int bookId, int memberId) =>
        await client.PostAsJsonAsync("/loans", new NewLoan(bookId, memberId));
}
