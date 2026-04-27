<!-- Linked issue (optional but appreciated): Closes #__ -->

## Summary

<!-- 1–3 sentences on the *why*, not just the what. The diff already shows the what. -->

## Checklist

- [ ] `dotnet test src/WKVRCProxy.HlsTests` passes locally.
- [ ] If behaviour changed, a test was added or updated to cover it.
- [ ] Ran `powershell -File build.ps1` end-to-end at least once on this branch (so the vendor pipeline + UI build are known good).
- [ ] Commit subjects pass `.githooks/commit-msg` — no duplicate `(YYYY.M.D.N-XXXX)` build-version stamps.
- [ ] No C# string interpolation in files patched by `build.ps1` regex (see GEMINI.md).

## Notes for relay / strategy / host changes

<!-- Delete this section if irrelevant. Otherwise: -->
<!-- - Did you change a host's relay-wrap behaviour? Mention the host. -->
<!-- - Did you add a host to the AVPro native-UA deny-list (`NativeAvProUaHosts`)? Why? -->
<!-- - Did you add or change a resolution strategy? Note the tier and any new vendored binary. -->
