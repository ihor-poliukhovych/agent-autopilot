# Azure DevOps

Dev Autopilot can read work items from Azure Boards and publish a passed change as a
**draft pull request** in Azure Repos. The agent never merges: a branch policy on the
target branch needs a person's approval, and the agent's own identity has no right to
get around it. This page sets that up step by step.

The examples use a made-up organisation `https://dev.azure.com/contoso`, project
`Shop` and repository `shop-api`. Use your own names.

## 1. Create a separate identity for the agent

Do not use your own account. The agent should act under an identity that has only the
rights it needs, so that Azure DevOps itself stops it from merging.

Pick one:

- **A service account user**: a separate Microsoft Entra (or Microsoft) account, for
  example `autopilot-agent@contoso.example`. Add it to the organisation in
  *Organization settings > Users > Add users* with the **Basic** access level, and add
  it to project `Shop` in the **Contributors** group.
- **A service principal**: an app registration in Microsoft Entra ID. Add it in
  *Organization settings > Users > Add users* (search for the app's name), access level
  **Basic**, project `Shop`, group **Contributors**. A service principal cannot have a
  personal access token; sign in with the Azure CLI instead (step 4, option B).

Do not add the identity to *Project Administrators*, *Project Collection
Administrators* or any group that can change policies or permissions.

## 2. Set the repository permissions

Open *Project settings > Repositories > shop-api > Security*, select the agent
identity (add it with *Add* if it is not listed) and set:

| Permission | Set to |
|---|---|
| Read | Allow |
| Contribute | Allow |
| Create branch | Allow |
| Contribute to pull requests | Allow |
| Force push (rewrite history and delete branches) | **Deny** |
| Bypass policies when pushing | **Deny** |
| Bypass policies when completing pull requests | **Deny** |
| Edit policies | **Deny** |
| Manage permissions | **Deny** |
| Create tag, Manage notes, Create / Delete / Rename repository, Remove others' locks | Not set or Deny |

An explicit Deny wins over an Allow the identity gets from a group, so set the five
**Deny** rows on the identity itself.

For work items, *Contributors* can already read, edit and comment on work items in
the project's area paths. Nothing else is needed.

## 3. Protect the target branch

Open *Project settings > Repositories > shop-api > Policies*, choose the target
branch (the `baseBranch` in `autopilot.json`, for example `main`) and turn on
**Require a minimum number of reviewers**:

- Minimum number of reviewers: **1** or more.
- **Allow requestors to approve their own changes**: off.
- Keep the policy **required** (not optional).

This is what makes a person approve every merge. `autopilot doctor` checks it.

## 4. Give the agent a token

The token is read only from a user environment variable whose name starts with
`AUTOPILOT_` (default `AUTOPILOT_ADO_PAT`), or from the Azure CLI. It is never written
to a file, a URL, a git config file or a log.

### Option A: personal access token (service account user)

1. Sign in to `https://dev.azure.com/contoso` **as the agent identity**.
2. Open *User settings > Personal access tokens > New Token*.
3. Organisation: `contoso` only. Expiration: as short as you can manage (for example
   30 days).
4. Scopes: *Custom defined*, then only:
   - **Work Items: Read & write**
   - **Code: Read & write**
   - **Project and Team: Read** (used by the project check in `autopilot doctor`)
5. Copy the token and store it as a **user** environment variable. In PowerShell:

   ```powershell
   [Environment]::SetEnvironmentVariable('AUTOPILOT_ADO_PAT', '<paste the token>', 'User')
   ```

   On Linux or macOS, add `export AUTOPILOT_ADO_PAT=...` to your shell profile, readable
   only by you.
6. Open a new terminal so the variable is visible.

Several projects can use different tokens: set `"tokenVariable": "AUTOPILOT_SHOP_PAT"`
in the settings and store the token under that name.

### Option B: Azure CLI (service principal or a user)

Install the Azure CLI, sign in as the agent identity (`az login`, or
`az login --service-principal ...` for a service principal) and set `"auth": "az"`.
Dev Autopilot then runs
`az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --query accessToken -o tsv`
for each command and sends the result as a Bearer token.

## 5. Settings in `.autopilot/autopilot.json`

The work item source and the publisher read the same settings block. Copy it into both:

```json
{
  "baseBranch": "main",
  "branchPrefix": "autopilot/",
  "modules": {
    "workItems": {
      "type": "azure-devops",
      "settings": {
        "organizationUrl": "https://dev.azure.com/contoso",
        "project": "Shop",
        "repository": "shop-api"
      }
    },
    "publisher": {
      "type": "azure-devops",
      "settings": {
        "organizationUrl": "https://dev.azure.com/contoso",
        "project": "Shop",
        "repository": "shop-api"
      }
    }
  }
}
```

| Setting | Required | Default | Meaning |
|---|---|---|---|
| `organizationUrl` | yes | | `https://dev.azure.com/<organisation>` |
| `project` | yes | | Project name or id |
| `repository` | for the publisher | | Repository name or id |
| `remote` | no | `origin` | The git remote that points to that repository |
| `query` | no | see below | WIQL query that lists the items the agent may look at |
| `tagPrefix` | no | `autopilot:` | Prefix of the triage tag |
| `recordTriage` | no | `remote` | `remote`: triage writes a tag and a comment on the work item. `local`: results stay in `.autopilot/out/triage/` and nothing is written to Azure DevOps (read-only) |
| `auth` | no | `pat` | `pat` (environment variable) or `az` (Azure CLI) |
| `tokenVariable` | no | `AUTOPILOT_ADO_PAT` | Name of the environment variable; must start with `AUTOPILOT_` |

Default query: open items of the project (state not Closed, Done or Removed) that have
the tag `dev-autopilot`, newest first, at most 50:

```sql
SELECT [System.Id] FROM WorkItems
WHERE [System.TeamProject] = @project
  AND [System.State] NOT IN ('Closed', 'Done', 'Removed')
  AND [System.Tags] CONTAINS 'dev-autopilot'
ORDER BY [System.CreatedDate] DESC
```

So a person decides which items the agent may look at by adding the tag `dev-autopilot`.
The name is chosen so that it does not start with the triage tag prefix `autopilot:`.

Open task files in `.autopilot/tasks/` are not read while the work items come from Azure
DevOps; `autopilot doctor` warns when there are some. `autopilot triage --source local`
(also `implement` and `run`) uses them for one run; drafts of that run stay local.

The git remote must point to the same organisation over https (or ssh). The token is
only ever sent to the host of `organizationUrl`; a push to any other https host is
refused.

## 6. What the agent does in Azure DevOps

- **Reads** the items from the query: title, description, acceptance criteria, tags,
  state and type. The HTML of the description and the acceptance criteria is turned
  into plain text.
- **Triage** replaces any tag that starts with `autopilot:` by `autopilot:agent-ready`
  or `autopilot:needs-human` (other tags stay) and adds one comment, marked
  "Written by Dev Autopilot", with the reason, the acceptance criteria and the risk.
  With `"recordTriage": "local"` it does neither: the result is only written to
  `.autopilot/out/triage/<id>.md` and `.json`, and the module sends no PATCH and no
  comment. Reading items still uses the query and batch endpoints (both POST requests
  that change nothing).
- **Publish** (only after the merge gate passed): pushes the agent branch
  (`autopilot/<id>`, never forced, no other branch) and opens a **draft** pull request
  `[autopilot] <title>` into the base branch, linked to the work item, with the same
  description as the local `PULL_REQUEST.md`. It never sets auto-complete, never votes
  and never completes the pull request.

## 7. `autopilot doctor`

| Check | OK when |
|---|---|
| Triage results | Always OK; says whether `recordTriage` is `remote` (tags and a comment) or `local` (nothing written) |
| Azure DevOps token | The environment variable is set, or `az` returns a token |
| Azure DevOps project | The project can be read with the token |
| Work item query | The query runs; shows how many items match and the tag used (`dev-autopilot` for the default query, or "custom query") |
| Azure DevOps repository | The repository can be read |
| Agent can Contribute / Create branch / Contribute to pull requests | The identity has the right |
| Agent cannot force push, edit policies, manage permissions, bypass policies when pushing, bypass policies when completing pull requests | The identity does **not** have the right; each one it has is a FAIL with the fix |
| Target branch needs a human approval | An enabled, required "Minimum number of reviewers" policy with at least 1 reviewer, where the author's vote does not count, applies to the base branch |

The rights are checked for the identity behind the token, with administrators not
treated as always allowed. If the token is not allowed to run the permission check,
those lines show SKIP; check the table in step 2 by hand.

## API reference

Written against Microsoft's public REST API documentation, api-version 7.1:

- [Wiql - Query By Wiql](https://learn.microsoft.com/rest/api/azure/devops/wit/wiql/query-by-wiql?view=azure-devops-rest-7.1)
- [Work Items - Get Work Items Batch](https://learn.microsoft.com/rest/api/azure/devops/wit/work-items/get-work-items-batch?view=azure-devops-rest-7.1)
- [Work Items - Update](https://learn.microsoft.com/rest/api/azure/devops/wit/work-items/update?view=azure-devops-rest-7.1)
- [Comments - Add Comment](https://learn.microsoft.com/rest/api/azure/devops/wit/comments/add-comment?view=azure-devops-rest-7.1) (7.1-preview.4)
- [Projects - Get](https://learn.microsoft.com/rest/api/azure/devops/core/projects/get?view=azure-devops-rest-7.1)
- [Repositories - Get Repository](https://learn.microsoft.com/rest/api/azure/devops/git/repositories/get-repository?view=azure-devops-rest-7.1)
- [Pull Requests - Create](https://learn.microsoft.com/rest/api/azure/devops/git/pull-requests/create?view=azure-devops-rest-7.1)
- [Policy Configurations - Get (Git)](https://learn.microsoft.com/rest/api/azure/devops/git/policy-configurations/get?view=azure-devops-rest-7.1)
- [Permissions - Has Permissions](https://learn.microsoft.com/rest/api/azure/devops/security/permissions/has-permissions?view=azure-devops-rest-7.1)
- [Security Namespaces - Query](https://learn.microsoft.com/rest/api/azure/devops/security/security-namespaces/query?view=azure-devops-rest-7.1) (Git Repositories bits)
- [Security namespace and permission reference](https://learn.microsoft.com/azure/devops/organizations/security/namespace-reference)
