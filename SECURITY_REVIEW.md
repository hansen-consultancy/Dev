# Security Review — HC.Dev (`dev` CLI tool)

> **Date:** 2026-06-10
> **Reviewer:** Claude Code (automated security review)
> **Scope:** Full source tree at `development` @ `33cce69` (working tree clean — whole-tool review, not a diff).
> **Companion doc:** `STRIDE.md` (threat model, v1.13.0). This review validates that model against the current code and adds findings the model under-rates or omits.

## Summary

The trust-gate refactor (v1.13.0) is solid: the `commands.json` execution path is genuinely gated, the decision logic is a pure, exhaustively-tested function, and the trust store is written atomically under a cross-process lock. The big-ticket "arbitrary command execution from a cloned repo" threat (S1/E1) is mitigated as documented.

The most important gap this review surfaces is **not** in the well-scrutinized `commands.json` path — it is in the **version-bump git path**, which the STRIDE model rates "Low" (T3) but which actually carries an **argument-injection vector**: attacker-influenced `<Version>` content from a cloned repo flows unsanitized into `git commit -m` and `git tag` argument strings. This is the one finding I'd fix before the next release.

Everything else is either already tracked in STRIDE at an appropriate severity, or is low-risk-by-design for a local developer tool.

| Severity | Count | Findings |
|----------|-------|----------|
| **Medium** | 1 | F1 — git argument injection via `<Version>` in bump/bump-commit *(✅ fixed v1.13.1)* |
| **Medium** | 1 | F2 — placeholder injection in custom commands (confirms STRIDE E2/T1) *(✅ mitigated, unreleased)* |
| **Low** | 3 | F3 (`--yes` trust bypass), F4 (trust TOCTOU), F5 (`:latest` Docker tag) *(✅ F5 mitigated, unreleased)* |
| **Informational** | 2 | F6 (unsafe JSON escaping), F7 (path-case trust keying on macOS) |

---

## F1 — Argument injection into `git` via `<Version>` content (Medium) — ✅ FIXED (v1.13.1, 2026-06-10)

> **Resolution:** `ProcessGitPort.Add/Commit/Tag` now route through `ProcSpec.ExecArgs` (each value is one `ArgumentList` token, never an interpolated argv string), with a `--` separator on `git add`. STRIDE T3 re-scoped to Medium → Mitigated. Build clean, 100/100 tests pass. Original finding preserved below for the record.

**Where:** `VersionBump.cs:ProcessGitPort.Commit` / `.Tag` / `.Add`, fed by `SemVer.ToString()` → `BumpPipeline.ResolveGit`.

**What:** Unlike the hardened shell/exec paths (`ProcSpec.Shell`, `ProcSpec.ExecArgs` use `ArgumentList`), the git port builds argument **strings** via interpolation:

```csharp
_runner.Run(ProcSpec.Exec("git", $"commit -m \"{message}\""), _ctx);  // message = "build: {version}"
_runner.Run(ProcSpec.Exec("git", $"tag {name}"), _ctx);               // name = "{version}", UNQUOTED
```

`ProcSpec.Exec` sets `psi.Arguments` directly (no `ArgumentList`), so the string is split into argv by the OS/runtime. The `version` is not a sanitized numeric value — `SemVer` carries `Suffix` and `BuildVariables` straight through from the parse regex, which is greedy and permissive:

```
(-(?<suffix>.*))?(\+(?<buildvars>.*))?$
```

So a `commands.json`-free repo can still steer this path purely through a crafted `.csproj`. Example `<Version>` values:

- `1.0.0-x --output=/etc/foo` → `git tag 1.0.0-x --output=/etc/foo`: the `--`-prefixed token is parsed by git as an **option**, not part of the tag name (option injection).
- A suffix containing `"` breaks out of the `-m "..."` quoting and lets the attacker append further git arguments to the commit invocation.

**Why it's not "Low" (T3):** STRIDE T3 only considers the regex *writing* an odd value into the csproj. The real exposure is that the parsed value is then handed to a **child process as command-line arguments**. There is no shell here, so this is argument/option injection, not full RCE — but `git` has option surfaces that reach the filesystem and config, and the bump path is exactly the one a developer runs after cloning. It belongs at Medium.

**Fix:**
1. Route the git port through `ProcSpec.ExecArgs("git", new[]{ "commit", "-m", message })` and `ExecArgs("git", new[]{ "tag", name })` so each value is delivered verbatim as one argv element (kills splitting/quoting breakout).
2. Additionally guard option injection: pass the tag name after `--` where supported, or reject version/tag strings beginning with `-`. `ArgumentList` alone does **not** stop a leading-dash token from being read as an option.
3. Consider validating that the bumped version matches a strict `^[0-9A-Za-z.\-+]+$` shape before it reaches git at all.

---

## F2 — Placeholder injection in custom commands (Medium — confirms STRIDE E2/T1) — ✅ MITIGATED (development, unreleased — 2026-06-20)

> **Resolution:** `RunCustomCommand` now gates substitution through `PlaceholderGuard.FindUnsafe` (`PlaceholderGuard.cs`): before any value is spliced into the trusted shell body, each *used* placeholder's resolved path is scanned for shell metacharacters (`& | ; < > \` $ " ' %` and CR/LF/NUL). A hit refuses the command with a `placeholder_unsafe` error (exit 5) and runs nothing — the trusted command body keeps its shell features (pipes/`&&`/redirects). We refuse rather than escape because cmd.exe quoting is not reliably composable and escaping collides with author-supplied quotes; `(`/`)` are intentionally allowed so paths like `Program Files (x86)` still work. Pure, exhaustively tested (`PlaceholderGuardTests.cs`); build clean, 146/146 tests pass. **Not yet released** — version still `1.13.0`. Original finding preserved below for the record.

**Where:** `Program.cs:ReplaceVariables` → `RunCustomCommand` → `ProcSpec.Shell`.

`{sln}`, `{project}`, `{dir}` are substituted into the command line **after** the trust hash is computed, with no escaping, then handed to `cmd.exe /S /C <cmd>` / `bash -c <cmd>`. The v1.13.0 hardening (delivering the command body as a single `ArgumentList` element instead of a re-quoted blob) stops *the wrapper* from being the injection point, but the command body is still a shell string, so a path containing `;`, `&&`, `$(…)`, or backticks executes inside that shell.

This matches STRIDE E2/T1 exactly and is correctly rated Medium there. Two points worth emphasising:

- **The trust hash covers the config file, not the resolved command.** A `commands.json` approved in a directory whose path later contains metacharacters (or a `{dir}` resolved from a symlinked/renamed checkout) executes the expanded form without re-prompting.
- In practice the attacker who controls the path usually also controls the shell, so the marginal risk is real but bounded. The fix is the one STRIDE already lists as priority #1: escape placeholder values, or model custom commands as argv rather than a shell string.

---

## F3 — `--yes` silently approves and persists untrusted configs (Low — confirms STRIDE E4)

`Trust.cs:TrustGate.Decide` returns `AutoApproved` for **both** `NewFile` and `ModifiedSinceTrust` when `--yes` is set, and `TrustGateOrchestrator` then commits the hash to the trust store. An agent or CI script that habitually passes `--yes` will trust a hostile `commands.json` on first sight, with no prompt and no stderr trace, and remember it.

This is the documented, opt-in automation escape hatch (E4) and the decision case is cleanly isolated, so a tighter policy is a one-line change. Recommended follow-ups (already noted in STRIDE):
- Refuse auto-approval of `NewFile` while still permitting `ModifiedSinceTrust`, or
- Add `--expected-hash=<hex>` so non-interactive callers pin what they're approving, and
- Emit a prominent stderr warning on every `--yes` auto-approval (currently silent in `--json` mode).

---

## F4 — Trust check / execution read are not the same read (Low, TOCTOU)

`Program.cs:43` parses `commands.json` via `File.ReadAllText` (this parsed list is what actually executes and what the prompt summary displays). `Trust.cs:TrustGateOrchestrator.Authorize` later re-reads the file via `File.ReadAllBytes` to compute the hash. Between the two reads the file can change on disk.

Impact is limited: the executed commands and the displayed summary both come from the *earlier* read, so an attacker can't show a benign summary and run a malicious one. The only consequence is hash/content drift causing a spurious re-prompt next run. Still, computing the hash over the **same bytes** that were parsed (read once, hash and deserialize from one buffer) removes the window cleanly and is a small change.

---

## F5 — Docker image pinned to `:latest` (Low — confirms STRIDE S3/I1) — ✅ MITIGATED (digest pin; development, unreleased — 2026-06-20)

> **Resolution:** The image is now pinned by digest — `…:latest@sha256:b89acec0…c93614f` via the `Program.FrontendImage` constant (`Program.cs`), resolved with `docker buildx imagetools inspect`. A repointed or compromised `:latest` tag can no longer reach the mounted source tree; updating the builder is now a deliberate digest bump + tool release (documented at the constant). The mount-narrowing half is **not** applied: `build-frontend.sh` lives at the workspace root and `cd`s into subfolders, so `/src` must remain the workspace root — narrowing isn't cleanly feasible without restructuring the build contract. Build clean, 146/146 tests pass. **Not yet released** — version still `1.13.0`. Original finding preserved below for the record.

`Program.cs:RunFrontend` / `Frontend.cs:FrontendBuild.Run` pull `ghcr.io/stevehansen/vidyano-frontend-builder:latest` and bind-mount the **entire** working directory (`-v {cwd}:/src`). A compromised or repointed `:latest` tag gets full read/write access to the source tree. The argv-form invocation (v1.13.0) correctly prevents the mount path from breaking into a shell — that part is good. Pin to a digest (`@sha256:…`) and, if feasible, mount only the needed subdirectory.

---

## F6 — `UnsafeRelaxedJsonEscaping` on the `--json` envelope (Informational)

`Program.cs:Finish` serializes with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`. The envelope embeds attacker-influenceable strings (`commandLine`, `stderrTail`, `stdoutTail`, file paths). The output is still valid JSON, but `<`, `>`, `&` are left unescaped. If a downstream consumer ever interpolates this JSON into HTML or a `<script>` block without re-encoding, it becomes an injection vector. For a CLI emitting to stdout this is informational; note it so consumers don't treat the output as HTML-safe.

---

## F7 — Trust keying uses path-case heuristic that over-trusts on macOS (Informational)

`Trust.cs:FileSystemTrustStore.PathComparer` uses `OrdinalIgnoreCase` on Windows and `Ordinal` elsewhere. The code comment acknowledges that default APFS on macOS is case-insensitive but there's no reliable runtime probe, so it picks the stricter `Ordinal`. The practical effect is the *safe* direction (a differently-cased path re-prompts rather than silently trusting), so this is informational — but worth flagging that trust entries are path-keyed, meaning moving/renaming a trusted checkout re-prompts, and two case-variant paths to the same file on case-insensitive macOS are treated as distinct trust entries.

---

## What's working well

- **Trust gate (S1/E1):** real gate, default-deny prompt, pure exhaustively-tested `Decide`, content-hash keyed. The decomposition into ports makes future policy changes (F3) cheap.
- **Trust store integrity (T5):** temp-file + atomic rename, cross-process file lock with monotonic-clock timeout, schema-versioned, corruption-tolerant reads. Well done.
- **Shell wrapper hardening (T1):** `ProcSpec.Shell` and `ProcSpec.ExecArgs` use `ArgumentList`, so the command-runner layer no longer concatenates untrusted strings into a quoted shell blob. The docker invocation (F5) correctly uses argv form.
- **Bump invariant:** `BumpPipeline` checks cross-project version agreement *before* any write or git side-effect, and short-circuits `git tag` if `git commit` fails — no orphan tags on the wrong commit.
- **CI publishing (S2):** OIDC trusted publishing with least-privilege `id-token: write` / `contents: read`, tag-triggered. No long-lived NuGet key in the repo.

## Recommended priority order

1. **F1** — switch `ProcessGitPort` to `ExecArgs` and guard against leading-dash version/tag tokens. (Only finding above informational with a real injection surface.)
2. ~~**F2** — escape placeholder values or model custom commands as argv (STRIDE priority #1, still open).~~ **Done (unreleased)** — `PlaceholderGuard` refuses paths carrying shell metacharacters before substitution.
3. ~~**F5** — pin the frontend Docker image to a digest.~~ **Done (unreleased)** — pinned via the `Program.FrontendImage` constant (`…@sha256:b89acec0…`).
4. **F3** — tighten `--yes` policy for `NewFile` and/or add `--expected-hash`.
5. **F4** — hash and parse `commands.json` from a single read.

> After applying F1, update `STRIDE.md`: re-scope **T3** (it currently describes only regex value-writing; the argument-injection-into-git aspect is the real risk) and bump its mitigation/severity accordingly.
