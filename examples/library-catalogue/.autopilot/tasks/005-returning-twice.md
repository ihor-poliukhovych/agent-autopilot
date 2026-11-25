# Returning a book twice changes the return date

Labels: bug

A volunteer scanned a book at the return desk that had already been returned last week.
The system said OK and moved the loan's return date to today, so our records now say the
book came back a week later than it did.

Expected: `POST /loans/{id}/return` for a loan that is already returned answers
`409 Conflict` with a problem details body and leaves `returnedAt` as it was. Returning an
active loan and returning an unknown loan (404) keep working as now.
