namespace Catalogue.Api.Store;

/// <summary>In-memory catalogue. One instance per app; every method is thread-safe.</summary>
public sealed class CatalogueStore
{
    /// <summary>A member may hold at most this many books at the same time.</summary>
    public const int MaxActiveLoans = 3;

    public static readonly TimeSpan LoanPeriod = TimeSpan.FromDays(14);

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<int, Book> _books = [];
    private readonly Dictionary<int, Member> _members = [];
    private readonly Dictionary<int, Loan> _loans = [];
    private int _nextBookId = 1;
    private int _nextLoanId = 1;

    public CatalogueStore(TimeProvider clock)
    {
        _clock = clock;
        Seed();
    }

    public IReadOnlyList<Book> ListBooks()
    {
        lock (_gate)
        {
            return _books.Values.OrderBy(b => b.Id).ToList();
        }
    }

    public Book GetBook(int id)
    {
        lock (_gate)
        {
            return _books[id];
        }
    }

    public Book AddBook(NewBook request)
    {
        lock (_gate)
        {
            var book = new Book(_nextBookId++, request.Isbn, request.Title, request.Author, request.Year, request.Copies);
            _books.Add(book.Id, book);
            return book;
        }
    }

    public Member? FindMember(int id)
    {
        lock (_gate)
        {
            return _members.GetValueOrDefault(id);
        }
    }

    public BorrowResult Borrow(int bookId, int memberId)
    {
        lock (_gate)
        {
            if (!_books.TryGetValue(bookId, out var book))
            {
                return BorrowResult.Failed(BorrowError.BookNotFound);
            }

            if (!_members.ContainsKey(memberId))
            {
                return BorrowResult.Failed(BorrowError.MemberNotFound);
            }

            var onLoan = _loans.Values.Count(l => l.BookId == bookId && l.ReturnedAt is null);
            if (onLoan >= book.Copies)
            {
                return BorrowResult.Failed(BorrowError.NoCopiesLeft);
            }

            var activeLoans = _loans.Values.Count(l => l.MemberId == memberId && l.ReturnedAt is null);
            if (activeLoans > MaxActiveLoans)
            {
                return BorrowResult.Failed(BorrowError.LoanLimitReached);
            }

            var now = _clock.GetUtcNow();
            var loan = new Loan(_nextLoanId++, bookId, memberId, now, now + LoanPeriod, ReturnedAt: null);
            _loans.Add(loan.Id, loan);
            return new BorrowResult(loan, BorrowError.None);
        }
    }

    /// <summary>Marks a loan as returned. Returns null when there is no such loan.</summary>
    public Loan? Return(int loanId)
    {
        lock (_gate)
        {
            if (!_loans.TryGetValue(loanId, out var loan))
            {
                return null;
            }

            var returned = loan with { ReturnedAt = _clock.GetUtcNow() };
            _loans[loanId] = returned;
            return returned;
        }
    }

    private void Seed()
    {
        AddBook(new NewBook("9780547928227", "The Hobbit", "J. R. R. Tolkien", 1937, 2));
        AddBook(new NewBook("9780547928210", "The Fellowship of the Ring", "J. R. R. Tolkien", 1954, 1));
        AddBook(new NewBook("9780547773742", "A Wizard of Earthsea", "Ursula K. Le Guin", 1968, 3));
        AddBook(new NewBook("9780441478125", "The Left Hand of Darkness", "Ursula K. Le Guin", 1969, 2));
        AddBook(new NewBook("9780441172719", "Dune", "Frank Herbert", 1965, 4));
        AddBook(new NewBook("9780441569595", "Neuromancer", "William Gibson", 1984, 1));

        foreach (var name in new[] { "Ada Lovelace", "Alan Turing", "Grace Hopper" })
        {
            var member = new Member(_members.Count + 1, name);
            _members.Add(member.Id, member);
        }
    }
}
