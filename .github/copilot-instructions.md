# Copilot instructions

`OpenDisplayNet` is a standalone `net10.0-windows10.0.22621.0` Bluetooth LE
library. Keep it independent of SideKick and do not add project references to
the parent repository.

- OpenDisplay uses service and characteristic UUID `00002446-0000-1000-8000-00805F9B34FB`.
- Treat these upstream repositories as the protocol source of truth:
  - [OpenDisplay protocol specification](https://github.com/OpenDisplay/opendisplay-protocol)
  - [OpenDisplay firmware and hardware documentation](https://github.com/OpenDisplay/Firmware)
  - [OpenDisplay web configuration and documentation](https://github.com/OpenDisplay/opendisplay.org)
  - [Reference Python client](https://github.com/OpenDisplay/py-opendisplay)
- Follow the protocol specification and reference client when changing packets,
  acknowledgements, compression, authentication, partial updates, or image
  encodings.
- Preserve explicit failures for GATT communication, command NACKs, and timeouts.
- Keep public APIs typed; avoid exposing raw protocol details unless the caller
  needs caller-encoded image data.
- Run the focused protocol tests in `tests/OpenDisplayNet.Tests` after changes.
