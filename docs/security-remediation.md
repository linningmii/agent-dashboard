# September 2026 security fixes

The review found two P1 issues. The outdated default branch was resolved by merging PR #1 into main (`e13eb46`). The remaining access-key session issue is addressed by server-side, hashed session tokens: logout revokes one session, credential rotation and switching authentication modes clear all sessions, and old stateless cookies are rejected. Sessions persist across restarts with the same key, expire after seven days, and are bounded to 1,024 active sessions. Live streams recheck authorization before sending snapshots and at each heartbeat.

The two runtime/dependency P2 findings are addressed as follows:

| Finding | Change |
|---|---|
| Vulnerable dependency versions | Microsoft.OpenApi 2.7.5; SQLitePCLRaw bundle 3.0.5 with SourceGear.sqlite3 3.53.4; js-yaml override 4.3.2 |
| Enrollment exhausts dashboard login quota | Independent dashboard-login and device-enrollment policies, each partitioned by actual network peer; forwarded headers cannot evade limits |

The SQLite bundle now obtains native binaries from SourceGear.sqlite3 rather than the vulnerable SQLitePCLRaw.lib.e_sqlite3 2.1.11 package. A regression test reads `sqlite_version()` from the actual loaded native library and requires at least 3.50.2 on every CI operating system.

Verification includes persisted session revocation, independent key-rotation rejection, expiry and legacy-cookie rejection, HTTP cookie replay after logout, already-open SSE shutdown, enrollment throttling without blocking a valid login, and rejection of forwarded-address spoofing. Existing protocol and private-tunnel checks remain in place.

Local results: 49 backend tests and 11 installer tests passed; a clean Enzyme install and production UI build passed. The independent OSV query of 170 resolved npm/NuGet entries returned no advisories. The isolated HTTP probe now returns 401 for both a logged-out cookie and a separate cookie issued before key rotation, while a valid login after ten invalid enrollment requests returns 200. The patched API and collector were deployed; all pre-restart completion IDs remain, and private-tunnel reporting, completion/replay, browser-origin checks and SSE passed again. Generated API contracts did not change.

The npm installer now runs in the frontend directory without `--prefix`; this avoids npm adding the current working directory as a local dependency during lockfile updates. Only the js-yaml package version changes in the committed lockfile.

The separate P2 finding about missing automated advisory monitoring is still outstanding; these dependency upgrades do not enable Dependabot or add an advisory-scanning CI gate. License selection, security reporting policy, public metadata review and repository protection are separate publication tasks. Repository visibility remains private.
