# Validation: Perene TTS Foundation

## Table of Contents
- [Acceptance Criteria](#acceptance-criteria)
- [Test Cases](#test-cases)
- [Manual Verification](#manual-verification)
- [Definition of Done](#definition-of-done)
- [Rollback Plan](#rollback-plan)
- [Execution Results](#execution-results)

## Acceptance Criteria
| Requirement | Acceptance Criterion |
| --- | --- |
| FR1 | .NET build passes and all three routes contain labeled controls and the archive-style responsive sidebar. |
| FR2 | Valid MP3 succeeds; malformed, silent, short, oversized, too-long references and unsupported languages fail. |
| FR3 | Saved voices retain UUID/name/reference/conditioning after restart; duplicate reference reuses identity. |
| FR4 | Creating each language produces its predefined spoken sample. |
| FR5 | Selected saved voice produces entered text; empty/oversized text and unknown/mismatched voice fail. |
| FR6 | Completed jobs expose playable/downloadable MP3 through the web host; invalid audio IDs cannot traverse storage. |
| FR7 | Polling shows loading, progress, completed/failed; concurrent submission returns busy and cannot switch conditioning. |
| FR8 | make docker-build, make docker-run-bg, make test and Compose validation succeed; volumes survive make docker-down. |
| FR9 | Native Mac worker selects MPS when available; Docker CPU reports CPU; web can reach host worker. |
| FR11 | History survives restart; legacy MP3s remain playable; library search, voice reuse, and localized previews work. |
| FR10 | README and AGENTS accurately document implemented layout, constraints, commands, and Learn rules. |

## Test Cases
**Unit/service tests:** pytest patterns from `book-notes-ia/services/ChatterboxTtsService/tests` with fake SynthesisEngine. Verify Swedish metadata, reference reuse, checksum invalidation, safe UUIDs, language validation, text bounds, busy locking, MP3 publication, per-job failure, and cleanup.

**Integration tests:** FastAPI TestClient covers upload/jobs/speech/audio with a fake engine and real FFmpeg-generated reference. .NET compilation and HTTP smoke checks cover routing and service registration. Real model synthesis requires an environment-backed test for each language.

## Manual Verification
1. Run `make docker-build`, `make test`, then `make docker-run-bg`; open http://localhost:8082.
2. Wait for ready; upload a 3–60 second spoken MP3, name it, and select English. Confirm progress and play/download preview.
3. Repeat for Portuguese and Swedish; verify native speakers' pronunciation manually.
4. Select each voice and generate new text. Submit a second operation while busy and inspect the error/recovery behavior.
5. Try malformed MP3, silence, short/long/oversized reference, empty text, and oversized text. Confirm useful errors and no partial published MP3.
6. Run `make docker-down` and `make docker-run-bg`; confirm saved names/voices and prior audio URLs persist.
7. Check narrow/mobile layout, keyboard navigation, light appearance, empty voices, worker unavailable, and initial model loading.
8. On Apple Silicon run `make mac-setup`, `make mac-worker` in one terminal, and `make mac-up` in another. Verify health reports MPS; repeat preview/text. macOS hardware verification is pending on this Steam Deck.
9. On Windows use WSL2/Docker Desktop Linux containers; on ARM64 macOS verify dependency image build and synthesis. These environments are pending until exercised there.

## Definition of Done
Spec documents, implementation, meaningful fake-engine tests, .NET compilation, Compose/Make validation, and actual validation results are recorded. Runtime model checks and target OS checks are explicitly reported if unavailable; no claim of real synthesis or Mac acceleration without execution. UI states, responsive styling, persistence, and current vendor guidance are documented.

## Rollback Plan
Run `make docker-down` to stop the new standalone stack. Preserve named data/model volumes for retry. For macOS stop the native worker and use `make docker-run-bg` for CPU Docker mode. Reference projects are untouched and require no rollback.

## Execution Results
Validated on 2026-10-08 on this Linux x86-64 Steam Deck through its Docker-compatible Podman daemon.

| Check | Result |
| --- | --- |
| Runtime worker Docker image | Built successfully with pinned Chatterbox source and resolved locked dependencies. |
| .NET 10 web image | Published successfully, including Blazor framework static assets during project-only Docker restore. |
| `make test` | 30 pytest tests passed; web image compilation passed. The final asset/reconnection changes were additionally compiled and browser-tested. |
| `make docker-check` | Standard and native-Mac web Compose configurations validate. |
| Bash/Python static checks | Mac launcher syntax and Python compilation pass. |
| HTTP smoke | Home and web health return 200; worker-unavailable state renders. |
| Real model startup | Pinned Multilingual V3 loads on CPU using the existing read-only model cache. |
| Real English voice creation | Eight-second excerpt of the example reference creates archived voice/conditioning and a valid nonempty MP3 preview. |
| Real custom text | The saved real voice synthesizes “Hello from Perene.” into a second MP3. |
| Chromium UI integration | Fake inference plus real FFmpeg: Swedish MP3 upload, predefined preview, text generation, audio retrieval/download disposition, and appearance toggle pass. Browser reports no page errors. |
| Responsive visual check | Desktop 1280px and mobile 390px screenshots inspected; no horizontal overflow. |
| Persistence | Automated tests reconstruct StudioService and verify voice/name/MP3 preservation; duplicate references retain identity and original name. |

Browser verification found and corrected a .NET 10 framework-asset omission caused by restoring only the project file before copying Razor components. `RequiresAspNetWebAssets=true`, MapStaticAssets, fingerprinted Assets URLs, and ImportMap are now part of the verified final implementation.

Pending target-environment checks: native Apple Silicon MPS execution/performance, Linux ARM64 image resolution, Windows Docker/WSL2 execution, and real-model Portuguese/Swedish speech quality. All three language flows pass fake-engine service tests; Swedish also passes the interactive browser flow. Real English generation was exercised; native-language listening review was not performed. Temporary smoke containers and their private test recordings are removed after verification. Neither sibling reference project is modified.

### Make Command and Startup Follow-up
The requested perene-archive-style targets are implemented, including foreground docker-run, detached docker-run-bg, docker-build/down/logs/ps/shell/exec/test/check, get-url, and explicitly destructive docker-reset. Original short targets remain aliases.

The DNS failure was reproduced inside the original running Podman TTS container. A test on the same network with explicit resolvers succeeded, so configurable worker DNS was added. The real application was rebuilt/recreated with all named volumes preserved. The recreated worker resolved huggingface.co and received HTTPS 200. Studio, worker health, and API documentation returned HTTP 200 at localhost:8082, localhost:5081/health, and localhost:5081/docs. The fresh model cache began downloading; readiness remains false during download/load, with no load_error at the verification point.

The regression suite now passes 32 tests, including recovery after transient load failures and cancellation of retries with bounded backoff. Both Compose configurations and the Make command recipes validate. Data Protection's unencrypted-local-key warning is documented separately from the resolved DNS error. Startup progress is checked separately from API reachability; HTTP 200 health alone does not mean model_ready=true.

### Blazor Watch-Mode Follow-up
The default standard/Mac web Compose service now selects the SDK development stage and mounts WebApp source, matching perene-archive's watch workflow. Verification on this Steam Deck observed polling enabled, hot reload enabled, a successful Debug build with zero warnings/errors, Development environment, and `Now listening on: http://0.0.0.0:8080` (host localhost:8082).

A temporary unused CSS custom property was appended to authored app.css. The running watcher reported the file update and applied static asset changes; an HTTP request returned the new CSS without rebuilding/recreating the container. The property was removed immediately afterward. Studio HTTP remained 200. `make test` passed 32 worker tests and compiled the separate published runtime stage. Both Compose files and Make recipes validate. No claim of Mac/Windows watch validation is made.

### Perene Design and Creation Library Follow-up
The requested archive design guide now supplies the dark/light tokens, Montserrat/Zilla Slab typography, and gold active navigation. The microphone logo reads PereneTTS. Separate routes provide voice creation (`/`), text generation (`/generate-audio`), and persistent audio/voice browsing (`/creations`). English uses the exact requested voice-test sentence; Portuguese/Swedish use equivalent translated previews.

Verification: 34 pytest tests pass, including all three language histories across StudioService/API reconstruction, failed-generation exclusion, legacy MP3/corrupt-sidecar fallback, and exact English preview. The .NET runtime image publishes successfully. Chromium with fake inference and real FFmpeg verifies creation, preview, generation, navigation while generating, MP3 download, history after browser reload, search, voice reuse, retained text draft, local font/icon assets, theme toggle, and mobile routes. No browser page errors or HTTP failures. Desktop 1440px and mobile 390px screenshots were inspected. Real-model pronunciation/hardware checks remain as documented above.

The final update was also applied to the real watch-mode stack with named volumes preserved. Blazor restarted, built with zero warnings/errors, and listened on container port 8080 (host 8082). Chromium verified the three live routes, locally loaded Montserrat/Zilla Slab, mobile engine readiness, and no horizontal overflow. Worker readiness returned true on CPU with no load error; GET /creations returned the previously saved audio. No test recordings were created in the real stack. Temporary synthetic-test containers/network were removed.
