# General Agent Guidelines

## ⚠️ HIGH PRIORITY RULES

- **Do not commit unless the user explicitly asks.** Even when the user explicitly requests a commit, that request applies only to the current conversation turn. Never treat commit as a default action after making changes.
- **All code and documentation must be written in English.** This includes code comments, commit messages, doc files, and any text the agent produces.
- **Never leave "Merge branch" commits in main** Follow Branch Merging Guidelines, use **Rebase + Fast-Forward** approach whenever not requesting a squash merge.

## Branch Merging
When merging changes from an agent-created branch or worktree back into `main`, adhere to these workflows:
  - By default, use **Rebase + Fast-Forward** merging to maintain a clean, linear commit history on `main` without creating merge commits. First, rebase the branch onto `main` (`git rebase main`), then checkout `main` and fast-forward merge the branch (`git merge <branch>`).
  - If the user explicitly requests a squash, use **Squash Merge** (`git merge --squash <branch>`). In this case, the agent must compose a clean, descriptive summary of the changes to be used as the commit message.

## Using Worktrees

When creating Git worktrees, create them under the `<repo>/worktrees/` directory. This keeps all worktrees inside the repository root and makes them easy to find and clean up. The `worktrees/` folder is ignored by `.gitignore` and must never be committed.

## IssueHistory

This is a folder to keep critical design changes or difficult issues that are easy to break again. They are there to remind developers and agents to be careful of some unobvious details.
When the user asks, the agent may summarize the key details while implementing the last request, and create a document in the `IssueHistory` folder, the file name pattern is `{YYMMDD}-{brief-title}.md`.

## Computer Use

You may have access to a Computer Use facility in your development environment.
Don't use it by default, only use it when a user asks. Such as:
* Please implement the plan and verify it using Computer Use.
* Please verify the feature after implementation using Computer Use in the coming tasks of this session.
