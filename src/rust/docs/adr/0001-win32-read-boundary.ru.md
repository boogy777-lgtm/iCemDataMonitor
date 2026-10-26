# ADR 0001 — минимальная Win32-граница чтения шины

Статус: принято для M2 development-инкремента, не полная промышленная приёмка.

Нужны точные Windows named section/mutex, read-only/unnamed fallback, timeout/abandoned,
побайтовая owned copy. Обычное чтение Data.dat через File::read не заменяет MMF.
Политика прикладного кода — forbid(unsafe_code); FFI требует отдельной доказуемой границы.

Проверены исходники winsafe 0.0.29, named-lock 0.4.1, win-sys 0.3.1 и win-sync-sys 0.0.2.
Они не предоставляют весь необходимый контракт одной подходящей safe-абстракцией.
В частности, winsafe ReadProcessMemory передаёт buffer.as_ptr() как writable pointer;
такой путь не принят. named-lock разрешает Send для guard Windows mutex, хотя ReleaseMutex
обязан выполняться на owning thread. Комбинация нескольких таких библиотек увеличила бы
поверхность доверия и старые версии windows crate; она не подключается.

Решение: один маленький first-party `icem-windows-native`, зависящий только от официального
windows-sys с точными features. Его публичные методы safe; unsafe — только Win32 FFI,
без dereference mapped pointers, from_raw_parts, transmute и unsafe impl Send/Sync.
Mapping копируется ReadProcessMemory в &mut-owned buffer через as_mut_ptr; никаких Rust
ссылок в externally mutable mapping не создаётся. Mutex guard !Send/!Sync, привязан к
NativeMutex borrow и освобождается на том же OS thread до decode/await.

Class: read-only Win32 mapping и mutex handles; окна/COM/raw writable mappings вне класса
(`crates/icem-windows-native/src/windows.rs`).
Inv: handle создан один раз/закрыт один раз, view unmap при любом выходе, имя UTF-16 без NUL,
copy bounded, guard !Send; RAII/compile-fail doc tests и Windows/.NET IPC tests.
N+1: новая OS операция добавляется только после отдельного invariant review; прикладные модули
не получают raw handles/pointers. Новые источники снимков используют существующий SnapshotSource.

Это **явное исключение** из first-party forbid: только native crate имеет deny(unsafe_code)
с локальным разрешением audited FFI module и deny(unsafe_op_in_unsafe_fn,
clippy::undocumented_unsafe_blocks). Все существующие 10 crates сохраняют forbid.
Workspace forbid не ослабляется; native crate имеет отдельный lint table именно потому,
что forbid не допускает локального override. Каждый unsafe block содержит обоснование.

Dependency: windows-sys 0.61.2 (MIT OR Apache-2.0), windows-link, Windows-only,
features Foundation/Memory/Threading/Diagnostics_Debug/FileSystem/Security. Это raw bindings без собственного
runtime; версии и checksum фиксируются Cargo.lock. CI сначала fetch --locked, далее offline.
Новый unsafe код не объявляется формально доказанным или независимым security-аудитом.
До release обязательны стенд с реальной AddIn, обычный/elevated token/DACL, стресс и ручной review.
