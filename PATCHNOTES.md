# Patch Notes

## 1.0.1 - 2026-07-26

- Replaced internal `WaitForEvent` wake-up and timeout interrupts with non-breaking `DEBUG_INTERRUPT.EXIT`, so event-pump cancellation no longer creates synthetic `CONTROL_C` target breaks.
- Preserved explicit debugger breaks as `DEBUG_INTERRUPT.ACTIVE`, including the MCP break workflow.
- Added explicit handling for `E_PENDING` and `S_FALSE` wait results so normal cancellation and timeout paths do not disable the pump or surface fake events.
- Avoided auto-continuing unknown debugger breaks unless they are classified as internal wake-ups.
- Corrected DbgEng execution-status value mapping and added focused regression tests for interrupt policy, wait-result classification, pump behavior, and first-chance exception defaults.
