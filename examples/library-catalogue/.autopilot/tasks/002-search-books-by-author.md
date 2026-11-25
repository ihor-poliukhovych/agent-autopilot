# Search books by author

Labels: feature

Librarians want to find every book by one author without scrolling the whole list.

Add an optional `author` query parameter to `GET /books`.

Acceptance criteria:

- `GET /books?author=<text>` returns only the books whose author contains `<text>`,
  ignoring upper and lower case. Example: `?author=tolkien` returns both Tolkien books and
  nothing else.
- The order is the same as the unfiltered list (by id).
- When no book matches, the answer is `200 OK` with an empty array, not 404.
- Without the parameter, or with an empty value, `GET /books` returns every book as it does today.
- No other endpoint changes.
