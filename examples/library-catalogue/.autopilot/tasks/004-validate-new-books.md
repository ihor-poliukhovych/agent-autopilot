# Validate new books

Labels: feature

Staff sometimes add books with a blank title or a mistyped ISBN, and the catalogue accepts
them. `POST /books` must check the request before it adds anything.

Rules:

- `title` must not be empty or only whitespace.
- `isbn`, after removing hyphens, must be exactly 10 or exactly 13 digits (0-9). Anything
  else is invalid, including letters, spaces and other lengths. (ISBN-10 check character
  `X` and checksum validation are out of scope.)
- An invalid request answers `400 Bad Request` with a validation problem details body
  (`application/problem+json`) whose `errors` names the invalid field (`title` or `isbn`),
  and the book is not added.
- A valid request works as today (`201 Created` with a `Location` header). The ISBN is
  stored as sent.
- No other endpoint changes.
