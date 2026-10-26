# Independent Windows counterparts (R3)

`windows_counterparts.py` is a mandatory Windows CI gate after the complete
Scaffold. It reuses the built Host and starts independent Mosquitto broker/CLI
and Python asyncua wire client. Reports and bounded logs upload with `always()`;
summary JSON is also printed. Windows runtime PASS must come from Actions,
never from Linux source checks. All inputs are test-only and affect cache identity;
no tool is added to the product payload, Rust dependency graph or system services.

## Pins, roles and review

`windows-counterparts.json` records official immutable source commit/archive
SHA256 and each exact CPython 3.12 Windows x64 wheel filename/URL/SHA256/license.
Wheel hashes were checked against official `https://pypi.org/pypi/NAME/VERSION/json`;
requirements enumerate the complete 13-wheel runtime closure with hashes.
Verification fetches these direct artifacts, then installs into a private venv
using `--no-index --no-deps --require-hashes --only-binary=:all:` and `pip check`.
No dependency resolution or source builds run during Python verification.

Mosquitto 2.0.22: official eclipse-mosquitto/mosquitto annotated v2.0.22 tag
`12e4aaacb7075c7c4425df01a1cfd69416187559`, commit
`766fa2c5a9bed6c65249cf555f430b982c417480`. Dual EPL-2.0/EDL-1.0;
LICENSE.txt, NOTICE.md, edl-v10 and epl-v20 preserved in evidence. Bundled
uthash/utlist are BSD-revised licensed (see upstream NOTICE.md); no plugins,
apps, C++ wrapper, TLS/PSK, cJSON, documentation or client threading.
`sub_client.c` uses `mosquitto_loop_forever`; this QoS2/retain/plain TCP contract
does not require Pthreads4W, OpenSSL or cJSON. Broker/library are upstream C and
contain native memory operations; this test process is outside application Rust
unsafe policy. Sources are unchanged and CMake/MSVC builds the real executables.

The pinned upstream CMake unconditionally names `OpenSSL::SSL` at
`lib/CMakeLists.txt:63` and `src/CMakeLists.txt:166` even with `WITH_TLS=OFF`.
`mosquitto-no-tls.cmake` supplies an empty imported INTERFACE target through
`CMAKE_PROJECT_mosquitto_INCLUDE`, guarded by explicit TLS=OFF. It provides no
headers, symbols or library, and makes no broker protocol changes. Successful
compile/link is required; missing TLS symbols cannot be satisfied by the hook.

Python roles/licenses (exact versions in manifest): asyncua LGPL-3.0-or-later is
the independent UA client; aiosqlite MIT backs its optional history code; anyio
MIT supplies async helpers; sortedcontainers Apache-2.0 provides sorted indexes;
python-dateutil BSD-3-Clause/Apache-2.0, six MIT, pytz MIT and tzdata Apache-2.0
support date/time handling; typing_extensions PSF-2.0 provides typing compatibility;
idna BSD-3-Clause supports anyio name handling. Cryptography
Apache-2.0 OR BSD-3-Clause and cffi MIT-0 / pycparser BSD-3-Clause are mandatory
asyncua crypto dependencies, even for the anonymous/None test profile.
Cryptography/cffi are prebuilt native wheels with Rust/C/FFI/OpenSSL internals;
they are independent test-tool boundaries, not application safe-Rust guarantees.
Wheel license notices stay in the private installed distributions. No Python or
crypto wheel is shipped with the native Windows product.

## Contract and scope

The probes preserve type/identity, raw NaN payload and negative zero, read-only
Write, inactive/active quality, absent SourceTimestamp, present ServerTimestamp,
COM stream time and writer metadata. MQTT checks QoS2, broker restart/reconnect,
unchanged bits/clocks and retained delivery after Host exit0. UA checks independent
browse/read/subscription and graceful Host exit0. Existing mandatory Host tests
cover UA source outage/lifecycle and MQTT all QoS/offline acquisition independently.
Fixture scope does not establish live Windows MMF/AddIn/hardware behavior.

Each probe uses its own temporary config/fixture, loopback ephemeral ports and
owned child handles. Host duration is derived from explicit startup/test budgets;
UA verification has an overall async deadline, MQTT CLI and broker boots have
explicit deadlines. Waiting for intentional Host duration proves graceful exit;
cleanup only terminates the probe's own outstanding child processes.
