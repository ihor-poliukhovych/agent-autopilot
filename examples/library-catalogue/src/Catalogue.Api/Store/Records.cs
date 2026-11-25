namespace Catalogue.Api.Store;

public sealed record Book(int Id, string Isbn, string Title, string Author, int Year, int Copies);

public sealed record Member(int Id, string Name);

public sealed record Loan(
    int Id,
    int BookId,
    int MemberId,
    DateTimeOffset LoanedAt,
    DateTimeOffset DueAt,
    DateTimeOffset? ReturnedAt);

public sealed record NewBook(string Isbn, string Title, string Author, int Year, int Copies);

public sealed record NewLoan(int BookId, int MemberId);

public enum BorrowError
{
    None,
    BookNotFound,
    MemberNotFound,
    NoCopiesLeft,
    LoanLimitReached,
}

public sealed record BorrowResult(Loan? Loan, BorrowError Error)
{
    public static BorrowResult Failed(BorrowError error) => new(null, error);
}
