# Attribution and third-party notices

Revit MCP is maintained and distributed by OOXGN.

## Original work

This project is derived from [Davud77/WEAM.Revit.AI](https://github.com/Davud77/WEAM.Revit.AI), authored by Davud77, at commit `c1bcd004c8fc690e901b831a72ec30268b28f66d`.

The original source is licensed under GNU Affero General Public License version 3 only (`AGPL-3.0-only`). The complete license is preserved in [LICENSE](LICENSE), and original authorship is documented in this notice. The original product name appears here solely to identify the upstream work.

## Modifications

Copyright 2026 OOXGN (GitHub: OOXGN-OOX) for modifications: Revit MCP product identity, installation and MCP identifiers, OOXGN branding, installer presentation and documentation. Modified code is distributed under `AGPL-3.0-only`.

OOXGN vector artwork is reused from the publisher's [AutocadTools](https://github.com/OOXGN-OOX/AutocadTools) project. OOXGN retains ownership of its names and marks; the software license does not grant trademark rights. Autodesk and Revit are trademarks of Autodesk, Inc. This project is independent of Autodesk.

## Redistributed components

- Node.js: distributed under its own license and bundled third-party terms; the installer includes `node/LICENSE`.
- .NET: Microsoft license and third-party notices are included under `licenses/` in the installer payload.
- `@modelcontextprotocol/server`, `@modelcontextprotocol/core` and `zod`: their original package license files are retained in the distributed dependency directories.
- Autodesk assemblies are referenced from an installed copy of Revit or the pinned build-only [Nice3point Revit API packages](https://github.com/Nice3point/revit-api). They are not redistributed in the plugin or installer.

The installer includes this notice, the project license and a corresponding-source archive. Models, user settings, bridge tokens and local development instructions are excluded from the source distribution.
