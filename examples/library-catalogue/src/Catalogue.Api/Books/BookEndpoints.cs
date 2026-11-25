using Catalogue.Api.Store;

namespace Catalogue.Api.Books;

public static class BookEndpoints
{
    public static IEndpointRouteBuilder MapBookEndpoints(this IEndpointRouteBuilder app)
    {
        var books = app.MapGroup("/books");
        books.MapGet("", ListBooks);
        books.MapGet("/{id:int}", GetBook);
        books.MapPost("", AddBook);
        return app;
    }

    private static IResult ListBooks(CatalogueStore store) => Results.Ok(store.ListBooks());

    private static IResult GetBook(int id, CatalogueStore store) => Results.Ok(store.GetBook(id));

    private static IResult AddBook(NewBook request, CatalogueStore store)
    {
        var book = store.AddBook(request);
        return Results.Created($"/books/{book.Id}", book);
    }
}
