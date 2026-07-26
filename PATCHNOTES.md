# Patch Notes

## 1.0.1 - 2026-07-26

- Fixed MCP-generated synthetic `CONTROL_C` breaks by removing periodic event-pump interrupts.
- Kept intentional target breaks for `kd_break` and detach recovery while using non-breaking wakeups for internal queued work.
- Made `kd_wait_for_event` observe the existing event pump instead of starting a second blocking wait.
- Fixed KD continue/state handling with `GO_HANDLED`, normalized running status reporting, and stale event cleanup.
- Captured first-chance breakpoint exceptions while preserving pass-through behavior for other first-chance exceptions.
- Improved `connect.py` / `disconnect.py` reliability and added regression tests for the KD event-handling paths.
