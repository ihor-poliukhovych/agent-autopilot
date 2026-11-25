# Opening a book that does not exist shows a server error

Labels: bug

Reported by the front desk.

When someone follows an old link to a book that was removed (or just types a wrong number,
for example `/books/42`), the app shows a generic "something went wrong" page instead of
"book not found". Our monitoring shows a 500 on `GET /books/{id}` every time, and the logs
have a `KeyNotFoundException` with a stack trace for each of these requests.

Expected: asking for a book id that is not in the catalogue answers `404 Not Found` with a
problem details body (like `GET /members/{id}` already does for an unknown member). Known
books must keep working as they do now.
