using System.Net;
using System.Net.Http.Json;
using Catalogue.Api.Store;

namespace Catalogue.Api.Tests;

public sealed class BookTests
{
    [Fact]
    public async Task List_returns_the_seeded_books_in_id_order()
    {
        await using var app = new CatalogueApp();
        var client = app.CreateClient();

        var books = await client.GetFromJsonAsync<List<Book>>("/books");

        Assert.NotNull(books);
        Assert.Equal(6, books.Count);
        Assert.Equal(Enumerable.Range(1, 6), books.Select(b => b.Id));
        Assert.Contains(books, b => b.Title == "The Hobbit" && b.Author == "J. R. R. Tolkien");
    }

    [Fact]
    public async Task Get_by_id_returns_the_book()
    {
        await using var app = new CatalogueApp();
        var client = app.CreateClient();

        var book = await client.GetFromJsonAsync<Book>("/books/5");

        Assert.NotNull(book);
        Assert.Equal("Dune", book.Title);
        Assert.Equal("Frank Herbert", book.Author);
        Assert.Equal(1965, book.Year);
        Assert.Equal("9780441172719", book.Isbn);
    }

    [Fact]
    public async Task Post_adds_a_book_and_returns_its_location()
    {
        await using var app = new CatalogueApp();
        var client = app.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/books",
            new NewBook("9780553293357", "Foundation", "Isaac Asimov", 1951, 2));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<Book>();
        Assert.NotNull(created);
        Assert.Equal(7, created.Id);
        Assert.Equal($"/books/{created.Id}", response.Headers.Location?.OriginalString);

        var fetched = await client.GetFromJsonAsync<Book>(response.Headers.Location);
        Assert.Equal(created, fetched);
    }

    [Fact]
    public async Task A_new_book_appears_in_the_list()
    {
        await using var app = new CatalogueApp();
        var client = app.CreateClient();

        await client.PostAsJsonAsync("/books", new NewBook("0441013597", "Dune Messiah", "Frank Herbert", 1969, 1));
        var books = await client.GetFromJsonAsync<List<Book>>("/books");

        Assert.NotNull(books);
        Assert.Equal(7, books.Count);
        Assert.Equal("Dune Messiah", books[^1].Title);
    }

    [Fact]
    public async Task A_non_numeric_id_does_not_match_the_route()
    {
        await using var app = new CatalogueApp();
        var client = app.CreateClient();

        var response = await client.GetAsync("/books/abc");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
