# Wiki source

The `.md` files in this folder are the **source of truth** for the GitHub Wiki at <https://github.com/RealWhyKnot/WKVRCProxy/wiki>. They are mirrored to the wiki repo by [`.github/workflows/wiki-sync.yml`](../../.github/workflows/wiki-sync.yml) on every push to `main` that touches `docs/wiki/**`.

## Why source-control the wiki?

- Wiki edits go through normal PR review.
- Wiki content has the same git history as the code it documents.
- Forks and mirrors get a complete copy of the docs without scraping.

## Editing rules

- Always edit here, never on the GitHub web UI for the wiki. Web edits will be **overwritten** by the next sync.
- Page filename = page name. Spaces become hyphens (`Resolution-Cascade.md` ⇒ "Resolution Cascade" page).
- Wiki-style links work: `[[Page Name]]` and `[[Page Name|alt text]]`.
- `_Sidebar.md` is the sidebar (special filename, recognised by GitHub).
- This `README.md` is **excluded** from the sync — GitHub Wiki uses `Home.md` as the landing page.

## Pages

- [Home](Home.md) — landing
- [Architecture](Architecture.md)
- [Resolution Cascade](Resolution-Cascade.md)
- [Relay Server](Relay-Server.md)
- [IPC and Redirector](IPC-and-Redirector.md)
- [Build Pipeline](Build-Pipeline.md)
- [Settings Reference](Settings-Reference.md)
- [Runtime State](Runtime-State.md)
- [Update and Uninstall](Update-and-Uninstall.md)
- [Engineering Standards](Engineering-Standards.md)
- [Troubleshooting](Troubleshooting.md)
- [AVPro vs Unity](AVPro-vs-Unity.md)
