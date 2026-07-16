# Documentation

- [`architecture.md`](architecture.md) — system overview: the three projects,
  their responsibilities, the threading model, and the end-to-end data flow.
- [`protocol.md`](protocol.md) — the TCP frame format and full JSON-RPC method
  catalogue (both directions), media streaming, and clock synchronization.
- [`design-decisions.md`](design-decisions.md) — why specific things are built
  the way they are, including a few decisions that reversed earlier ones once
  the actual behavior turned out to be wrong.
- Open feature/robustness/tooling ideas now live as
  [GitHub Issues](https://github.com/JonasTrampe/RpgTimeTracker/issues)
  instead of a local todo list.
- [`legal-todo.md`](legal-todo.md) — open licensing/attribution tasks (About
  dialog, third-party notices) and the checklist to run through whenever a
  new dependency is added.

These reflect the software as it currently stands and should be updated
alongside any change to the network protocol, the clock/media pipelines, or
the project structure.
