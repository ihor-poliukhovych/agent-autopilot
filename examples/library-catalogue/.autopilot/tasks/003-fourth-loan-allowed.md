# Members can borrow a fourth book

Labels: bug

Our rule is that a member may have at most 3 books at home at the same time. Yesterday a
member walked out with 4: the system let the fourth loan through. Only the fifth one was
refused with "A member may hold at most 3 books at a time".

Expected: with 3 active (not yet returned) loans, `POST /loans` for that member answers
`409 Conflict` with a problem details body. Returned loans do not count, so after returning
one book the member can borrow again. The other loan rules (unknown book or member, no
copies left) stay as they are.
