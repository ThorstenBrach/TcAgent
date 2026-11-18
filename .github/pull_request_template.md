<!--
Thanks for the pull request! Please fill in every section. A check reads this description: the PR stays red until
the sections are filled in and every box is ticked (or the line is marked "n/a").
Please leave out customer, machine and project names - a pull request is public.
-->

## What and why

<!-- What does this change do, and which problem does it solve? -->

## Issue

<!-- "Fixes #123" or "Related to #123". Larger changes start with an issue, so the idea can be discussed first. -->

## Type of change

- [ ] Bug fix
- [ ] New feature or tool
- [ ] Change of existing behaviour
- [ ] Documentation only
- [ ] Build, pipeline or maintenance

## How it was tested

<!-- Unit tests, a test in Visual Studio 2022 and/or the XAE Shell, TwinCAT version, a pipeline run ... -->

## Checklist

<!-- Tick every line, or write "n/a" at the end of a line that does not apply. -->

- [ ] `dotnet test tests\TcAgent.Tests` passes
- [ ] A line in `CHANGELOG.md` under "Unreleased"
- [ ] Docs updated (`docs/TOOLS.md`, `docs/USER_GUIDE.md`, `agents/TCAGENT_GUIDE.md` and `SKILL.md` in sync)
- [ ] A new tool is registered, has a class in `src/Policy.cs` and a module in `src/Modules.cs`
- [ ] Windows and dialogs follow `docs/UI_GUIDELINES.md` (a new window started with an approved mockup)
- [ ] No customer, company, machine or person names in code, docs, tests, screenshots or commit messages
- [ ] No Beckhoff binaries or generated interop assemblies added
