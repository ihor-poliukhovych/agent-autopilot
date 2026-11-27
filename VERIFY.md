# Verification before publishing

Checks run on 2026-11-26 before the repository was made public.

| Check | How | Result |
|---|---|---|
| Secrets in files and history | search of every commit's patch for key and token formats (`sk-ant-`, `ghp_`, `github_pat_`, private key headers, storage account keys) | none found |
| Employer and client words | search of every commit's patch for a list of names that must not appear | none found |
| Local paths | search of every commit's patch for the author's local user and drive paths | none found |
| Commit identity | `git log --all --format='%an <%ae>\|%cn <%ce>'` | one personal identity only |
| Project data | no `.autopilot/` folder of any real project in the repository; project settings and tasks live outside it | ok |
| Tests | `dotnet test Autopilot.slnx` | 689 passed, 0 failed |
| Example tests | `dotnet test examples/library-catalogue/Catalogue.slnx` | 19 passed, 0 failed |

## Dependency licences

All are permissive and compatible with this repository's MIT licence (from each package's NuGet metadata):

| Package | Licence |
|---|---|
| System.CommandLine | MIT |
| Microsoft.Extensions.FileSystemGlobbing | MIT |
| ModelContextProtocol | Apache-2.0 |
| xunit, xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |
| coverlet.collector | MIT |
| Microsoft.AspNetCore.Mvc.Testing (example only) | MIT |

## Not yet done

- A fresh clone installed and run end to end by [docs/GETTING-STARTED.md](docs/GETTING-STARTED.md)
  on a clean machine. The steps were run on the author's machine only.
