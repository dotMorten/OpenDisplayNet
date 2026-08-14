# Copilot instructions

`OpenDisplayNet` is a standalone `net10.0-windows10.0.22621.0` Bluetooth LE
library. Keep it independent of SideKick and do not add project references to
the parent repository.

- OpenDisplay uses service and characteristic UUID `00002446-0000-1000-8000-00805F9B34FB`.
- Follow the local OpenDisplay protocol references when changing packets,
  acknowledgements, compression, authentication, or image encodings.
- Preserve explicit failures for GATT communication, command NACKs, and timeouts.
- Keep public APIs typed; avoid exposing raw protocol details unless the caller
  needs caller-encoded image data.
- Run the focused protocol tests in `tests/OpenDisplayNet.Tests` after changes.
