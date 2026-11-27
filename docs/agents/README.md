# Agent guide

- Check the working tree before editing and preserve other people's changes.
- Use the [business processes](../domain/business-processes.md) and [domain invariants](../domain/domain-invariants.md) for domain decisions. Use the [pitch](../pitch/README.md) for demo scope.
- Follow the [source layout](../../src/README.md). Coordinate changes to shared identifiers, formats and units across .NET and Python.
- Label simulated and estimated data. Keep credentials, personal locations and large source datasets out of Git.
- Follow the [Git workflow](../../CONTRIBUTING.md). Run the checks relevant to the task and report results and limitations.

Codex, Claude Code, Gemini CLI and Copilot have discovery files at their expected repository paths. They point to [AGENTS.md](../../AGENTS.md) for shared context. For chat-based tools, include the relevant files in the prompt.
