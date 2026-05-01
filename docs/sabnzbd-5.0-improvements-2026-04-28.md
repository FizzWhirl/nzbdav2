# SABnzbd 5.0 Release Notes — Applicability to nzbdav

Date: 2026-04-28
Status: Deep-dive complete. Implementation outcomes recorded inline below.

This document maps each SABnzbd 5.0.0 release-note item against the current
nzbdav codebase and rates whether it is applicable, already addressed, or
out of scope. Items are ranked by potential value to nzbdav.

Architectural reminder: nzbdav **streams** segments on demand; it never
assembles a file on disk. So any SAB feature that targets disk assembly,
on-disk caches, or the post-processing pipeline (unrar / unpack /
verification) is out of scope by design.

---

## High-value items (worth implementing)

### 1. NNTP Pipelining (SAB 5.0 headline feature)
**SAB description:** "Eliminates idle waiting between requests, significantly
improving speeds on high-latency connections." New servers default to
`Articles per request = 2`.

**nzbdav status:** **Not implemented.** The vendored `UsenetSharp` library
([`UsenetClient.ArticleAsync.cs`](backend/Libs/UsenetSharp/UsenetSharp/Clients/UsenetClient.ArticleAsync.cs),
[`UsenetClient.BodyAsync.cs`](backend/Libs/UsenetSharp/UsenetSharp/Clients/UsenetClient.BodyAsync.cs))
issues one BODY/STAT per round-trip. nzbdav compensates with high
per-stream connection counts (default 25), which masks per-connection RTT
on most setups, but each individual connection still pays full RTT
between articles.

**Why it matters here:** On high-latency Usenet routes (e.g., Asia/Pacific
to EU/US providers, or VPN'd connections), pipelining 2–4 BODY commands
per connection can deliver 30–60% per-connection throughput improvement
without using more connections — directly improving total stream
bandwidth at the same connection count, which also reduces memory
(fewer parallel buffers needed for the same speed).

**Cost:** Non-trivial. Requires modifying the NNTP client at the
protocol layer to:
  - track in-flight requests per connection,
  - maintain ordered-response parsing,
  - handle the case where one request in the pipeline fails / the
    connection drops mid-pipeline.

**Recommendation:** Worth a focused investigation — could be the single
biggest streaming-throughput improvement available.

---

### 2. PAR2-only-missing should not block (SAB 5.0 bug fix)
**SAB description:** "If only par2 files were missing, jobs could get
incorrectly aborted."

**nzbdav status:** **Plausibly affected.** PAR2 files in nzbdav are used
purely as a filename oracle ([`GetPar2FileDescriptorsStep`](backend/Queue/DeobfuscationSteps/2.GetPar2FileDescriptors/GetPar2FileDescriptorsStep.cs)).
We don't need them to stream. But the [`ProviderErrorService`](backend/Services/ProviderErrorService.cs)
records a `MissingArticles` event and may flip
`HasBlockingMissingArticles=true` on PAR2-named files just as it would
on real media files — there's no special-casing for `.par2`.

**Why it matters here:** A `.par2`/`.vol*` file becoming "blocking"
should not be surfaced in the UI as a blocking failure for the job: the
actual streamable content (the RAR set or media file) is unaffected. If
any consumer code uses `HasBlockingMissingArticles` to make decisions
about the job, a missing PAR2 will incorrectly fail the job.

**Cost:** Small. Either:
  - skip recording missing-article events for files matching the
    `.par2` / `.vol*+*.par2` regex in
    [`Par2.Par2FilenameRegex`](backend/Par2Recovery/Par2.cs), or
  - keep recording but exclude PAR2 files from the
    `HasBlockingMissingArticles` aggregation rollup.

**Recommendation:** Implement when convenient — defensive correctness
fix, low risk.

---

### 3. Reduced delays during job transitions (SAB 5.0)
**SAB description:** "Reduced delays between jobs during post-processing."

**nzbdav status:** **Mostly fine, one tunable.** The queue main loop
([`QueueManager.ProcessQueueAsync`](backend/Queue/QueueManager.cs#L132))
already polls back-to-back when items are available; only when the
queue is empty does it sleep for 1 minute, and that sleep is awoken
immediately by `_sleepingQueueToken` cancellation when a new item is
added. So inter-job latency for back-to-back items is already near-zero.

One real delay: [`MediaAnalysisService`](backend/Services/MediaAnalysisService.cs#L41)
has a hard `Task.Delay(3000)` after each analysis. If this runs in the
queue critical path for each completed item, that's a fixed 3-second
tax per job.

**Recommendation:** Investigate whether the 3-second `MediaAnalysisService`
delay is necessary; if it's just a "let things settle" pause, drop it.

---

## Medium-value items

### 4. Bounded preflight check ("Check before download could get stuck")
**SAB description:** "Check before download could get stuck or fail to reject."

**nzbdav status:** Equivalent functionality lives in
[`HealthCheckService`](backend/Services/HealthCheckService.cs) (background
batch validation) and the per-segment STAT walks in
[`MultiProviderNntpClient`](backend/Clients/Usenet/MultiProviderNntpClient.cs).
The recently-added
[`[MultiProvider] Segment ... missing across all N providers`](backend/Clients/Usenet/MultiProviderNntpClient.cs)
warning makes stalls visible; what's missing is a hard ceiling on how
long a single `STAT` walk can run before being aborted with a clear
"could not determine availability" status rather than silently retrying.

**Recommendation:** Add an overall walk timeout (e.g. 30 s) to
`RunFromPoolWithBackup` so a STAT/BODY across N providers cannot
indefinitely consume a streaming worker.

---

### 5. No tracebacks in browser (SAB 5.0)
**SAB description:** "No longer show tracebacks in the browser, only in
the logs."

**nzbdav status:** **Largely already true.** Stack traces are written
only to logs ([`QueueManager.cs:192`](backend/Queue/QueueManager.cs#L192))
or to console output of CLI tools. ASP.NET's developer exception page
should not be enabled in `Program.cs` for production — verify that
`UseDeveloperExceptionPage()` is gated on `IsDevelopment()` only.

**Recommendation:** Verify [`Program.cs`](backend/Program.cs) does not
expose stack traces in API error responses; if any controller catches
and returns `ex.ToString()` in a JSON body, sanitize it.

---

### 6. Disk-full handling (SAB 5.0)
**SAB description:** "Improved handling of disks getting full."

**nzbdav status:** Mostly N/A — nzbdav doesn't write media files, but it
*does* write the SQLite DB and config. A disk-full on `/config` will
cause silent data corruption / crash. There's no explicit free-space
check in [`DatabaseMaintenanceService`](backend/Services/DatabaseMaintenanceService.cs).

**Recommendation:** Cheap addition — emit a Warning if free space on
the config volume drops below e.g. 100 MB.

---

### 7. Inconsistent file sorting inside jobs (SAB 5.0 bug fix)
**SAB description:** "Sorting of files inside jobs was inconsistent."

**nzbdav status:** Likely affects multipart/RAR aggregation
([`Queue/DeobfuscationSteps`](backend/Queue/DeobfuscationSteps/)). Worth
a targeted code review of the multipart sorter to confirm a stable
order (alphabetical with natural-number sort) is being used for
`.partN.rar` / `.rXX` patterns regardless of input order.

**Recommendation:** Audit existing sort to ensure natural sort is
applied; medium effort.

---

### 8. Non-NFC unicode filenames (SAB 5.0 bug fix)
**SAB description:** "Improved handling of non-NFC unicode filenames."

**nzbdav status:** nzbdav matches NZB filenames against database paths
in many places ([`ProviderErrorService.NormalizeFilenameForGrouping`](backend/Services/ProviderErrorService.cs#L194)
already normalizes by stripping extensions, but does not Unicode-
normalize). If two paths differ only by NFC vs NFD composition (common
when macOS clients are involved), they will not match.

**Recommendation:** Apply `string.Normalize(NormalizationForm.FormC)`
inside `NormalizeFilenameForGrouping` and at the file-ingestion site
in the deobfuscation steps. Low cost, low risk.

---

## Items already addressed or N/A

| SAB 5.0 item | nzbdav status | Notes |
|---|---|---|
| Direct Write (assemble to disk) | **N/A** | nzbdav never writes media files. |
| Post-processing scripts always run | **N/A** | nzbdav doesn't expose user PP scripts. |
| Removed `empty_postproc` setting | **N/A** | Same as above. |
| Article cache redesign | **Different model** | nzbdav has [`SharedStreamEntry`](backend/Streams/SharedStreamEntry.cs) + [`BufferedSegmentStream`](backend/Streams/BufferedSegmentStream.cs); see memory-usage-improvement-report-v2 for ongoing tuning. |
| Diskspace check includes category folders | **N/A** | No on-disk download folders. |
| NZB-of-NZBs naming | **N/A** | Not a use case here. |
| Encrypted-RAR detection | Already handled via [`PasswordProtectedRarException`](backend/Exceptions/PasswordProtectedRarException.cs). |
| Aborted Direct Unpack | **N/A** | No Direct Unpack. |
| Unrar password length | **N/A** | We don't shell out to unrar; we use `SharpCompress`. |
| Tray icon / macOS launch / Python upgrades | **N/A** | Not a Python/desktop app. |
| `verify_xff_header` default | **Already safe** | Loopback bypass in [`WebApplicationAuthExtensions.cs`](backend/Auth/WebApplicationAuthExtensions.cs#L37) requires both `IPAddress.IsLoopback(RemoteIpAddress)` AND a constant-time-compared `FRONTEND_BACKEND_API_KEY` token, so a spoofed loopback is insufficient. |
| Minimum free space default 500M | **N/A** | No download folder. |

---

## Suggested ordering for implementation

1. **PAR2-only-missing should not flip blocking** — small, defensive, low risk.
2. **Drop or shorten the 3 s `MediaAnalysisService` delay** if not load-bearing.
3. **Bounded preflight walk timeout** in `RunFromPoolWithBackup`.
4. **Unicode NFC normalization** of filenames at ingestion.
5. **Disk-free warning** for the config volume.
6. **NNTP Pipelining** — biggest potential win, biggest cost; do last as a focused project.

---

## Implementation outcomes (2026-04-28)

| # | Item | Outcome | Commit | Reason |
|---|---|---|---|---|
| 1 | NNTP Pipelining | **Deferred** | — | Vendored UsenetSharp uses `AsyncSemaphore _commandLock = new(1)` to enforce strict per-connection request-response sequencing, with the lock held for the entire body stream. Pipelining would require replacing the lock with an ordered request queue, demultiplexing a single shared response-reader loop into N `Pipe`s, changing the public API to support batching, and updating every call site. Multi-day project, high risk of silent body-misattribution bugs. nzbdav's typical 25-connections-per-stream model already amortizes RTT across parallelism, so the benefit is concentrated on low-connection / high-RTT users. Flag as a future focused project. |
| 2 | PAR2 should not flip `HasBlockingMissingArticles` | **Implemented** | [`5bac1da8`](https://github.com/FizzWhirl/nzbdav2/commit/5bac1da8) | Confirmed `HasBlockingMissingArticles` is consumed only by the stats UI ("critical" badge) and the missing-articles list filter — it doesn't gate downloads. PAR2 in nzbdav is metadata-only (no Reed-Solomon recovery), so missing PAR2 should never be flagged critical. Added `IsPar2Filename` helper and skipped the flip in both `PersistEvents` and `BackfillSummariesAsync`. |
| 3 | Drop 3 s `MediaAnalysisService` delay | **Skipped** | — | Re-read of [`MediaAnalysisService.AnalyzeMediaAsync`](backend/Services/MediaAnalysisService.cs#L41) showed the `Task.Delay(3000)` is **only on the retry path** when ffprobe returned an empty result on the first attempt — it's a per-failure backoff, not a per-job tax. Removing it would cause more rapid retries on transient provider hiccups. |
| 4 | Bounded provider-walk timeout | **Skipped** | — | Each provider's per-operation timeout is already configurable via `GetUsenetOperationTimeout()` (default 90 s, applied per provider via [`MultiConnectionNntpClient.GetDynamicTimeout`](backend/Clients/Usenet/MultiConnectionNntpClient.cs#L72)). Health-check is already bounded via `cts.CancelAfter`. Adding an outer cap on the streaming-worker walk would risk false-failing legitimately slow-but-eventually-succeeding fetches. The new `[MultiProvider] Segment ... missing across all N providers` warning gives users the data they need to tune the existing timeout knob. |
| 8 | Unicode NFC normalization of filenames | **Implemented** | [`8a78f529`](https://github.com/FizzWhirl/nzbdav2/commit/8a78f529) | `NormalizeFilenameForGrouping` is the join key that maps events to summaries; mixed NFC/NFD variants of the same filename would create duplicate summary rows whose evidence bitsets never converge. Added `string.Normalize(NormalizationForm.FormC)` at function entry. Limited scope to this one function — DB-write paths are out of scope. |
