using Catalogue.Api.Store;

namespace Catalogue.Api.Loans;

public static class LoanEndpoints
{
    public static IEndpointRouteBuilder MapLoanEndpoints(this IEndpointRouteBuilder app)
    {
        var loans = app.MapGroup("/loans");
        loans.MapPost("", Borrow);
        loans.MapPost("/{id:int}/return", Return);
        return app;
    }

    private static IResult Borrow(NewLoan request, CatalogueStore store)
    {
        var result = store.Borrow(request.BookId, request.MemberId);
        return result.Error switch
        {
            BorrowError.None => Results.Created($"/loans/{result.Loan!.Id}", result.Loan),
            BorrowError.BookNotFound => Results.Problem($"Book {request.BookId} was not found.", statusCode: StatusCodes.Status404NotFound),
            BorrowError.MemberNotFound => Results.Problem($"Member {request.MemberId} was not found.", statusCode: StatusCodes.Status404NotFound),
            BorrowError.NoCopiesLeft => Results.Problem("Every copy of this book is on loan.", statusCode: StatusCodes.Status409Conflict),
            BorrowError.LoanLimitReached => Results.Problem(
                $"A member may hold at most {CatalogueStore.MaxActiveLoans} books at a time.",
                statusCode: StatusCodes.Status409Conflict),
            _ => throw new InvalidOperationException($"Unknown borrow result {result.Error}."),
        };
    }

    private static IResult Return(int id, CatalogueStore store) =>
        store.Return(id) is { } loan
            ? Results.Ok(loan)
            : Results.Problem($"Loan {id} was not found.", statusCode: StatusCodes.Status404NotFound);
}
